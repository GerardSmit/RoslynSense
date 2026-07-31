using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using StreamJsonRpc;

namespace RoslynMCP.Lsp;

/// <summary>
/// Live LSP sessions in this process, so non-LSP code (MCP tools running in the daemon) can
/// push edits to editors. Core use: a tool that would write a file the user has open routes
/// the change through <c>workspace/applyEdit</c> instead — writing disk under a dirty editor
/// buffer would race the user's unsaved edits.
/// </summary>
internal static class LspSessionRegistry
{
    private static readonly ConcurrentDictionary<string, JsonRpc> s_sessions = new(StringComparer.Ordinal);

    public static void Register(string sessionId, JsonRpc rpc) => s_sessions[sessionId] = rpc;

    public static void Unregister(string sessionId) => s_sessions.TryRemove(sessionId, out _);

    public static bool HasSessions => !s_sessions.IsEmpty;

    /// <summary>
    /// Routes a debug command from an LLM client into the editor's own debug session
    /// (server→client request <c>roslynSense/editorDebugCommand</c>). First session that
    /// executes it wins; <c>null</c> when no connected editor could handle it.
    /// </summary>
    public static async Task<string?> TryInvokeEditorDebugCommandAsync(
        Protocol.EditorDebugCommandParams p, CancellationToken ct)
    {
        foreach (var rpc in s_sessions.Values)
        {
            try
            {
                var result = await rpc.InvokeWithParameterObjectAsync<string?>(
                    "roslynSense/editorDebugCommand", p, ct);
                if (result is not null)
                    return result;
            }
            catch (Exception ex) when (ex is RemoteInvocationException or ConnectionLostException or ObjectDisposedException)
            {
                // Session gone or client has no debug session — try the others.
            }
        }
        return null;
    }

    /// <summary>
    /// Replaces the full content of <paramref name="filePath"/> in the editor(s) that have it
    /// open, via <c>workspace/applyEdit</c>. Returns true when an editor applied the edit (the
    /// editor will follow up with didChange, which refreshes the shared overlay). False when
    /// no session applied it — the caller should fall back to writing the file on disk.
    /// </summary>
    public static async Task<bool> TryApplyFullTextEditAsync(
        string filePath, string newText, string label, CancellationToken ct)
    {
        if (s_sessions.IsEmpty || !OpenDocumentStore.TryGet(filePath, out var currentText))
            return false;

        // Replace the editor's CURRENT buffer (the overlay text), not the disk text — the
        // range must span what the editor actually holds.
        var lastLine = currentText.Lines[currentText.Lines.Count - 1];
        var fullRange = new Protocol.Range(
            new Position(0, 0),
            new Position(currentText.Lines.Count - 1, lastLine.SpanIncludingLineBreak.Length));

        var edit = new WorkspaceEdit(new Dictionary<string, TextEdit[]>
        {
            [LspConverters.PathToUri(filePath)] = [new TextEdit(fullRange, newText)],
        });

        bool applied = false;
        foreach (var rpc in s_sessions.Values)
        {
            try
            {
                var result = await rpc.InvokeWithParameterObjectAsync<ApplyWorkspaceEditResult>(
                    "workspace/applyEdit", new ApplyWorkspaceEditParams(label, edit), ct);
                applied |= result.Applied;
            }
            catch (Exception ex) when (ex is RemoteInvocationException or ConnectionLostException or ObjectDisposedException)
            {
                // Session gone or client refused — try the others / fall back to disk.
            }
        }
        return applied;
    }
}

public sealed record ApplyWorkspaceEditParams(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("edit")] WorkspaceEdit Edit);

public sealed record ApplyWorkspaceEditResult(
    [property: JsonPropertyName("applied")] bool Applied,
    [property: JsonPropertyName("failureReason")] string? FailureReason);

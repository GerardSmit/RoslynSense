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
    private static readonly ConcurrentDictionary<string, LspServer> s_servers = new(StringComparer.Ordinal);

    public static void Register(string sessionId, JsonRpc rpc, LspServer server)
    {
        s_sessions[sessionId] = rpc;
        s_servers[sessionId] = server;
    }

    public static void Unregister(string sessionId)
    {
        s_sessions.TryRemove(sessionId, out _);
        s_servers.TryRemove(sessionId, out _);
    }

    public static bool HasSessions => !s_sessions.IsEmpty;

    /// <summary>Snapshot of the connected editors' RPC channels.</summary>
    internal static IReadOnlyList<JsonRpc> ActiveSessions() => s_sessions.Values.ToList();

    /// <summary>
    /// Asks every connected editor to re-request derived data. Used by background work that
    /// completes after a response was already sent — analyzer diagnostics landing in the cache,
    /// a workspace reload — where the client otherwise keeps showing a stale answer.
    /// Each session honors only the refresh kinds it declared support for.
    /// </summary>
    public static async Task RequestRefreshAsync(RefreshKind kinds, CancellationToken ct = default)
    {
        foreach (var server in s_servers.Values)
        {
            try { await server.RefreshClientAsync(kinds, ct); }
            catch (Exception ex) when (ex is RemoteInvocationException or ConnectionLostException or ObjectDisposedException)
            {
                // Session gone — the others still get their nudge.
            }
        }
    }

    /// <summary>
    /// Tells every connected editor that the set of loaded projects moved, so anything drawn from
    /// it is redrawn. Distinct from <see cref="RequestRefreshAsync"/>, which asks the client to
    /// re-pull the LSP-standard derived data (lenses, hints, diagnostics) and has no way to say
    /// "your tree is stale": the Solution Explorer is a custom view and the protocol knows nothing
    /// about it.
    /// </summary>
    public static void NotifyProjectSetChanged()
    {
        foreach (var rpc in s_sessions.Values)
        {
            // Not awaited and not thrown from: this runs on the tail of a background load, and a
            // client that has gone away must not fault it.
            try { _ = rpc.NotifyWithParameterObjectAsync("roslynSense/projectSetChanged", new { }); }
            catch (Exception ex) when (ex is ConnectionLostException or ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>Long enough to swallow a burst of analyzer passes, short enough that squiggles
    /// appear while the user is still looking at the line that produced them.</summary>
    private static readonly TimeSpan RefreshQuiet = TimeSpan.FromMilliseconds(750);

    /// <summary>Ceiling on the debounce. Without one, a steady stream of requests closer together
    /// than the quiet period never fires at all — a long analyzer sweep over a large open set
    /// starves the refresh it exists to deliver.</summary>
    private static readonly TimeSpan RefreshMaximumWait = TimeSpan.FromSeconds(5);

    private static readonly object s_refreshGate = new();
    private static CancellationTokenSource? s_pendingRefresh;
    private static RefreshKind s_pendingKinds;
    private static DateTime s_firstPendingUtc;

    /// <summary>
    /// <see cref="RequestRefreshAsync"/>, coalesced. Callers that fire once per document must use
    /// this: a refresh is not a per-document message — it tells the editor to re-pull
    /// <em>everything</em>, including a full <c>workspace/diagnostic</c> sweep. Sending one per
    /// document turned opening a folder of ten files into ten whole-workspace re-pulls.
    /// </summary>
    public static void ScheduleRefresh(RefreshKind kinds)
    {
        CancellationTokenSource cts;
        TimeSpan delay;

        lock (s_refreshGate)
        {
            // Union, so a coalesced burst still asks for everything its members wanted.
            if (s_pendingKinds == default)
                s_firstPendingUtc = DateTime.UtcNow;
            s_pendingKinds |= kinds;

            // Guarded: the current source is disposed by its own task once that task is done with
            // it, and cancelling a disposed source throws — which would escape this method and
            // leave the refresh unscheduled entirely.
            try { s_pendingRefresh?.Cancel(); }
            catch (ObjectDisposedException) { }

            // Not disposed here: the superseded task may still be inside its RPC holding this
            // token, and disposing it under the gate made StreamJsonRpc throw
            // ObjectDisposedException on a refresh whose kinds had already been drained — so those
            // kinds were lost and nothing ever sent them. Each task disposes its own.
            cts = s_pendingRefresh = new CancellationTokenSource();

            var waited = DateTime.UtcNow - s_firstPendingUtc;
            delay = waited >= RefreshMaximumWait ? TimeSpan.Zero : RefreshQuiet;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cts.Token);

                RefreshKind kindsToSend;
                lock (s_refreshGate)
                {
                    kindsToSend = s_pendingKinds;
                    s_pendingKinds = default;
                }

                if (kindsToSend == default)
                    return;

                // Deliberately not the debounce token: past this point the kinds have been taken
                // out of the pending set, so this task owns them and must deliver them even if a
                // newer request supersedes the debounce a moment later.
                await RequestRefreshAsync(kindsToSend);
            }
            catch (OperationCanceledException)
            {
                // Superseded before draining — the later call carries these kinds and will send them.
            }
            catch (Exception)
            {
                // A client that cannot be told is not a reason to fault background work.
            }
            finally
            {
                // Stop pointing at a source that is about to become invalid, so the next caller
                // creates a fresh one rather than cancelling this one.
                lock (s_refreshGate)
                {
                    if (ReferenceEquals(s_pendingRefresh, cts))
                        s_pendingRefresh = null;
                }

                cts.Dispose();
            }
        });
    }

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

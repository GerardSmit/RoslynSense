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
    private static readonly Services.Debouncer s_refreshDebounce = new("Lsp");
    private static RefreshKind s_pendingKinds;
    private static DateTime s_firstPendingUtc;

    /// <summary>What asked for the refreshes currently pending, and when recent ones were sent —
    /// a refresh loop presents as an editor that re-pulls forever with nothing in the log naming
    /// the instigator, so every send remembers its reasons and heavy traffic gets summarized.</summary>
    private static readonly List<string> s_pendingReasons = new();
    private static readonly Queue<(DateTime Utc, string[] Reasons)> s_recentSends = new();
    private static readonly TimeSpan SendWindow = TimeSpan.FromMinutes(10);
    private static int s_sendsSinceLogged;

    /// <summary>
    /// <see cref="RequestRefreshAsync"/>, coalesced. Callers that fire once per document must use
    /// this: a refresh is not a per-document message — it tells the editor to re-pull
    /// <em>everything</em>, including a full <c>workspace/diagnostic</c> sweep. Sending one per
    /// document turned opening a folder of ten files into ten whole-workspace re-pulls.
    /// </summary>
    /// <param name="reason">Short slug naming why, for the traffic summary — "analyzer-pass-stored",
    /// "workspace-reload". A refresh storm's log line is only as useful as these are honest.</param>
    public static void ScheduleRefresh(RefreshKind kinds, string? reason = null)
    {
        TimeSpan delay;
        lock (s_refreshGate)
        {
            // Union, so a coalesced burst still asks for everything its members wanted.
            if (s_pendingKinds == default)
                s_firstPendingUtc = DateTime.UtcNow;
            s_pendingKinds |= kinds;
            s_pendingReasons.Add(reason ?? "unspecified");

            var waited = DateTime.UtcNow - s_firstPendingUtc;
            delay = waited >= RefreshMaximumWait ? TimeSpan.Zero : RefreshQuiet;
        }

        s_refreshDebounce.Restart(delay, async _ =>
        {
            RefreshKind kindsToSend;
            string[] reasons;
            lock (s_refreshGate)
            {
                kindsToSend = s_pendingKinds;
                s_pendingKinds = default;
                reasons = [.. s_pendingReasons];
                s_pendingReasons.Clear();
            }

            if (kindsToSend == default)
                return;

            NoteRefreshSent(reasons);

            try
            {
                // Deliberately not the debounce token: past this point the kinds have been taken
                // out of the pending set, so this task owns them and must deliver them even if a
                // newer request supersedes the debounce a moment later.
                await RequestRefreshAsync(kindsToSend);
            }
            catch (Exception)
            {
                // A client that cannot be told is not a reason to fault background work.
            }
        });
    }

    /// <summary>
    /// One summary line per 50 refreshes: how many went out in the last ten minutes and on whose
    /// behalf. Each refresh costs the client a re-pull of every open document plus a workspace
    /// sweep, so sustained volume is always worth a name in the log — a background loop asking
    /// over and over used to be invisible until someone counted sweeps by hand.
    /// </summary>
    private static void NoteRefreshSent(string[] reasons)
    {
        lock (s_recentSends)
        {
            var now = DateTime.UtcNow;
            s_recentSends.Enqueue((now, reasons));
            while (s_recentSends.Count > 0 && now - s_recentSends.Peek().Utc > SendWindow)
                s_recentSends.Dequeue();

            if (++s_sendsSinceLogged < 50)
                return;
            s_sendsSinceLogged = 0;

            var histogram = s_recentSends
                .SelectMany(send => send.Reasons)
                .GroupBy(r => r, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key}×{g.Count()}");

            LspLog.Info(
                $"{s_recentSends.Count} client refreshes in the last {SendWindow.TotalMinutes:0} min "
                + $"(requests: {string.Join(", ", histogram)}).");
        }
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

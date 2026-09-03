using RoslynMCP.Daemon;

namespace RoslynMCP.Services.Debugging;

/// <summary>
/// Fallback path for the MCP debug tools: when the chat owns no debug session but the user is
/// debugging in the editor, commands route through the shared daemon into the editor's debug
/// session (extension → DAP). Chain: this MCP client → daemon pipe (<c>Kind = "editor-debug"</c>)
/// → connected LSP session → VSCode debug adapter.
/// </summary>
internal static class EditorDebugRouter
{
    /// <summary>The editor's debug state for the current working directory's solution, or
    /// <c>null</c> when the user is not debugging in the editor.</summary>
    public static EditorDebugStateStore.State? ActiveEditorSession()
    {
        var state = EditorDebugStateStore.ReadNearest(Directory.GetCurrentDirectory());
        return state is { Active: true } ? state : null;
    }

    /// <summary>
    /// Routes one debug command into the editor's session. Returns <c>null</c> when there is
    /// no active editor debug session (caller falls back to its usual "no session" error).
    /// </summary>
    public static async Task<string?> TryRouteAsync(
        string action, Dictionary<string, string>? args = null, CancellationToken ct = default)
    {
        if (ActiveEditorSession() is null)
            return null;

        string? solutionKey = HostPaths.ResolveSolutionKey(Directory.GetCurrentDirectory());
        if (solutionKey is null)
            return null;

        var pipe = await DaemonSpawner.ConnectOrSpawnAsync(solutionKey, ct);
        if (pipe is null)
            return "Error: The user is debugging in the editor, but the shared host is unreachable.";

        await using (pipe)
        {
            var request = new DaemonRequest(
                Guid.NewGuid().ToString("N"), action, args ?? new Dictionary<string, string>(),
                "markdown", Kind: "editor-debug");
            await IpcProtocol.WriteMessageAsync(pipe, request, ct);
            var response = await IpcProtocol.ReadMessageAsync<DaemonResponse>(pipe, ct);
            if (response is null)
                return "Error: The shared host closed the connection.";
            return response.Ok
                ? $"[Editor debug session] {response.Result}"
                : $"Error: {response.Error}";
        }
    }
}

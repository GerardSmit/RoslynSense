using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services.Debugging;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>
/// The editor side of the debug bridge: lists LLM-owned debug sessions
/// (<see cref="DebugStateStore"/>), forwards editor commands into the owning MCP client
/// process over its command pipe, and records the editor's own debug-session state for
/// LLM clients (<see cref="EditorDebugStateStore"/>).
/// </summary>
internal static class DebugBridgeHandler
{
    public static DebugSessionInfo[] Sessions() =>
        DebugStateStore.List()
            .Select(e => new DebugSessionInfo(
                e.OwnerPid, e.Kind, e.Target, e.State, e.Reason, e.Function,
                e.FilePath, e.Line, e.UpdatedAtUtc.ToString("O"),
                (e.Breakpoints ?? []).Select(b =>
                    new DebugBreakpointInfo(b.Id, b.File, b.Line, b.Condition)).ToArray()))
            .ToArray();

    public static async Task<DebugCommandResult> CommandAsync(DebugCommandParams p, CancellationToken ct)
    {
        var response = await DebugCommandPipeServer.SendAsync(
            p.OwnerPid,
            new DebugPipeRequest(
                p.Action, p.Expression, p.File, p.Line, p.Condition, p.BreakpointId,
                p.HitCondition, p.LogMessage, p.FrameId, p.VariablesReference, p.Value, p.Filters),
            ct);
        return response.Ok
            ? new DebugCommandResult(true, response.Result ?? "")
            : new DebugCommandResult(false, response.Error ?? "Unknown error.");
    }

    public static void SyncBreakpoints(SyncBreakpointsParams p)
    {
        string? solution = File.Exists(p.SolutionPath)
            ? p.SolutionPath
            : Daemon.HostPaths.ResolveSolutionKey(p.SolutionPath);
        if (solution is null)
            return;

        SharedBreakpointStore.Write(solution, p.Breakpoints
            .Select(b => new SharedBreakpointStore.Breakpoint(b.File, b.Line, b.Condition))
            .ToArray());
    }

    public static void EditorState(EditorDebugStateParams p)
    {
        // The extension may only know its workspace folder — resolve to the owning solution
        // so the store key matches what MCP tools derive from their working directory.
        string? solution = File.Exists(p.SolutionPath)
            ? p.SolutionPath
            : Daemon.HostPaths.ResolveSolutionKey(p.SolutionPath);
        if (solution is null)
            return;

        if (!p.Active)
        {
            EditorDebugStateStore.Clear(solution);
            return;
        }

        EditorDebugStateStore.Write(solution, new EditorDebugStateStore.State(
            Active: true, p.SessionName, p.AdapterType, p.ExecutionState,
            p.Reason, p.FilePath, p.Line, DateTime.UtcNow));
    }
}

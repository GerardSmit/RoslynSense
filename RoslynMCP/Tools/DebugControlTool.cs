using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

[McpServerToolType]
[InProcessOnly]
public static class DebugControlTool
{
    /// <summary>
    /// Controls execution flow: continue, stepping, pause, run-to-location, and moving the
    /// instruction pointer, dispatched on <c>action</c>.
    /// </summary>
    [McpServerTool, Description(
        "Drive execution in the active debug session. action: 'continue' (default) runs to the " +
        "next breakpoint; 'step_in'/'step_over'/'step_out' step; 'pause' suspends a running " +
        "target (infinite loop, deadlock) to see where it is; 'run_until' sets a temporary " +
        "breakpoint at filePath:line (optional condition) that auto-removes once hit; " +
        "'run_to_cursor' runs to filePath:line without leaving a breakpoint behind — it does not " +
        "stop again on the next lap round a loop; 'set_next_statement' moves the instruction " +
        "pointer to filePath:line without executing the code in between (.NET Framework only). " +
        "Returns the current pause location with code context.")]
    public static async Task<string> DebugContinue(
        IOutputFormatter fmt,
        [Description("Action: continue (default), step_in, step_over, step_out, pause, run_until, run_to_cursor, set_next_statement.")]
        string action = "continue",
        [Description("Source file path. Required for run_until, run_to_cursor, set_next_statement.")]
        string? filePath = null,
        [Description("Line number. Required for run_until, run_to_cursor, set_next_statement.")]
        int line = 0,
        [Description("run_until only: optional condition expression, only stop when it evaluates to true (e.g. 'i == 42').")]
        string? condition = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string normalized = action.ToLowerInvariant();

            if (normalized is "run_until" or "run_to_cursor" or "set_next_statement")
            {
                if (filePath is not { Length: > 0 } || line <= 0)
                    return $"Error: action '{normalized}' requires filePath and line.";
            }

            var session = DebugSessionManager.GetSession();
            if (session is null)
            {
                // The user may be debugging in the editor — route the command there.
                var routed = normalized switch
                {
                    "run_until" => await Services.Debugging.EditorDebugRouter.TryRouteAsync("run_until",
                        new Dictionary<string, string>
                        {
                            ["file"] = filePath!,
                            ["line"] = line.ToString(),
                            ["condition"] = condition ?? "",
                        }, cancellationToken),
                    "run_to_cursor" or "set_next_statement" => null,
                    _ => await Services.Debugging.EditorDebugRouter.TryRouteAsync(normalized, ct: cancellationToken),
                };
                return routed ?? "Error: No active debug session. Use DebugStartTest or DebugAttach first.";
            }

            if (normalized == "run_until")
                return await RunUntilAsync(session, filePath!, line, condition, fmt, cancellationToken);

            var result = normalized switch
            {
                "continue" => await session.ContinueAsync(cancellationToken),
                "step_in" => await session.StepInAsync(cancellationToken),
                "step_over" => await session.StepOverAsync(cancellationToken),
                "step_out" => await session.StepOutAsync(cancellationToken),
                "pause" => await session.InterruptAsync(cancellationToken),
                "run_to_cursor" => await session.RunToLocationAsync(filePath!, line, cancellationToken),
                "set_next_statement" => await session.SetNextStatementAsync(filePath!, line, cancellationToken),
                _ => (string?)null
            };

            if (result is null)
                return $"Error: Unknown action '{action}'. Use: continue, step_in, step_over, step_out, pause, run_until, run_to_cursor, set_next_statement.";
            var sb = new StringBuilder(result);
            if (normalized == "set_next_statement")
            {
                fmt.AppendHints(sb,
                    "Nothing between the old and new position has run — locals keep their values",
                    "Use DebugContinue with action='step_over' to execute from the new position");
            }
            else
            {
                fmt.AppendHints(sb,
                    "Use DebugEvaluate to inspect variables",
                    "Use DebugStatus to see current position");
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Stops the active debug session, letting the debuggee shut itself down first.
    /// </summary>
    [McpServerTool, Description(
        "Stop the active debug session and clean up all debugger processes. The debuggee is asked "
        + "to shut down cleanly — hosted services get their StopAsync — and killed only if it does "
        + "not exit in time.")]
    public static async Task<string> DebugStop()
    {
        var session = DebugSessionManager.GetSession();
        if (session is null)
        {
            // Never stop the editor's session from here: it belongs to the user.
            return Services.Debugging.EditorDebugRouter.ActiveEditorSession() is not null
                ? "No LLM-owned debug session. The user is debugging in the editor — that session " +
                  "is theirs to stop. Use DebugContinue/DebugEvaluate to work with it instead."
                : "No active debug session.";
        }

        var (_, message) = await session.ShutdownAsync(DebugStopTimeout);
        DebugSessionManager.DisposeSession();
        return message;
    }

    /// <summary>How long a debuggee gets to shut itself down before it is killed. The same budget
    /// the editor's stop button uses, so both surfaces end a session the same way.</summary>
    internal static readonly TimeSpan DebugStopTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Sets a temporary breakpoint at the given location, continues execution until it is hit,
    /// and automatically removes the breakpoint. If a different breakpoint is hit first,
    /// the temporary breakpoint is kept and the user is informed.
    /// </summary>
    private static async Task<string> RunUntilAsync(
        IDebugBackend session,
        string filePath,
        int line,
        string? condition,
        IOutputFormatter fmt,
        CancellationToken cancellationToken)
    {
        // Set temporary breakpoint
        var (setMessage, bpId) = await session.SetBreakpointAsync(filePath, line, condition, cancellationToken: cancellationToken);

        if (bpId is null)
            return $"Error setting temporary breakpoint: {setMessage}";

        // Continue execution
        var continueResult = await session.ContinueAsync(cancellationToken);

        // Check which breakpoint (if any) was hit
        var frame = session.CurrentFrame;
        bool hitTargetBreakpoint = frame is not null && frame.BreakpointNumber == bpId.Value;
        bool programExited = frame is null || frame.Reason == "exited" || frame.Reason == "exited-normally";

        if (hitTargetBreakpoint || programExited)
        {
            // Auto-remove the temporary breakpoint
            try { await session.RemoveBreakpointAsync(bpId.Value, cancellationToken); }
            catch { /* Best-effort removal */ }
            var sbHit = new StringBuilder(continueResult);
            fmt.AppendHints(sbHit,
                "Use DebugEvaluate to inspect variables",
                "Use DebugStatus to see current position");
            return sbHit.ToString();
        }

        // A different breakpoint was hit — keep the temp breakpoint active
        {
            var sbOther = new StringBuilder(continueResult + $"\n\n_Note: Stopped at a different breakpoint. " +
                $"Temporary breakpoint #{bpId.Value} at {Path.GetFileName(filePath)}:{line} is still active._");
            fmt.AppendHints(sbOther,
                "Use DebugEvaluate to inspect variables",
                "Use DebugStatus to see current position");
            return sbOther.ToString();
        }
    }
}

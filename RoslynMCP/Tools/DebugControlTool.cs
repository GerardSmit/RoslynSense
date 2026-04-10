using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

[McpServerToolType]
public static class DebugControlTool
{
    /// <summary>
    /// Controls execution flow: continue, step in, step over, or step out.
    /// </summary>
    [McpServerTool, Description(
        "Continue execution in the active debug session. " +
        "Runs until the next breakpoint is hit or the program exits. " +
        "Returns the current pause location with code context.")]
    public static async Task<string> DebugContinue(
        IOutputFormatter fmt,
        [Description("Action to perform: 'continue' (default), 'step_in', 'step_over', or 'step_out'.")]
        string action = "continue",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var session = DebugSessionManager.GetSession();
            if (session is null)
                return "Error: No active debug session. Use DebugStartTest or DebugAttach first.";

            var result = action.ToLowerInvariant() switch
            {
                "continue" => await session.ContinueAsync(cancellationToken),
                "step_in" => await session.StepInAsync(cancellationToken),
                "step_over" => await session.StepOverAsync(cancellationToken),
                "step_out" => await session.StepOutAsync(cancellationToken),
                _ => (string?)null
            };

            if (result is null)
                return $"Error: Unknown action '{action}'. Use: continue, step_in, step_over, step_out.";
            var sb = new StringBuilder(result);
            fmt.AppendHints(sb,
                "Use DebugEvaluate to inspect variables",
                "Use DebugStatus to see current position");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Stops the active debug session.
    /// </summary>
    [McpServerTool, Description(
        "Stop the active debug session and clean up all debugger processes.")]
    public static string DebugStop()
    {
        var session = DebugSessionManager.GetSession();
        if (session is null)
            return "No active debug session.";

        var result = session.Stop();
        DebugSessionManager.DisposeSession();
        return result;
    }

    /// <summary>
    /// Sets a temporary breakpoint at the given location, continues execution until it is hit,
    /// and automatically removes the breakpoint. If a different breakpoint is hit first,
    /// the temporary breakpoint is kept and the user is informed.
    /// </summary>
    [McpServerTool, Description(
        "Run until a specific location is reached. Sets a temporary breakpoint, continues execution, " +
        "and automatically removes the breakpoint once hit. " +
        "If a different breakpoint is hit first, the temporary breakpoint remains active. " +
        "Optionally accepts a condition expression so the breakpoint only triggers when the condition is true.")]
    public static async Task<string> DebugRunUntil(
        [Description("Path to the source file.")] string filePath,
        [Description("Line number to run until.")] int line,
        IOutputFormatter fmt,
        [Description("Optional condition expression. Only stop when this evaluates to true (e.g. 'i == 42').")]
        string? condition = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var session = DebugSessionManager.GetSession();
            if (session is null)
                return "Error: No active debug session. Use DebugStartTest or DebugAttach first.";

            // Set temporary breakpoint
            var (setMessage, bpId) = await session.SetBreakpointAsync(filePath, line, condition, cancellationToken);

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
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}

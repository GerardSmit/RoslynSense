using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

[McpServerToolType]
public static class DebugInspectTool
{
    /// <summary>
    /// Evaluates an expression in the current debug frame.
    /// </summary>
    [McpServerTool, Description(
        "Evaluate an expression in the current debug context. " +
        "The debugger must be paused at a breakpoint or after stepping.")]
    public static async Task<string> DebugEvaluate(
        [Description("Expression to evaluate (e.g. 'myVar.Count', 'x + y', 'obj.ToString()').")]
        string expression,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var session = DebugSessionManager.GetSession();
            if (session is null)
                return "Error: No active debug session. Use DebugStartTest or DebugAttach first.";

            return await session.EvaluateAsync(expression, cancellationToken);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Gets local variables in the current debug frame.
    /// </summary>
    [McpServerTool, Description(
        "Get local variables and their values in the current debug frame. " +
        "The debugger must be paused at a breakpoint or after stepping.")]
    public static async Task<string> DebugLocals(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var session = DebugSessionManager.GetSession();
            if (session is null)
                return "Error: No active debug session. Use DebugStartTest or DebugAttach first.";

            return await session.GetLocalsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Gets the call stack in the current debug frame.
    /// </summary>
    [McpServerTool, Description(
        "Get the call stack showing the chain of function calls that led to the current position. " +
        "The debugger must be paused at a breakpoint or after stepping.")]
    public static async Task<string> DebugStackTrace(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var session = DebugSessionManager.GetSession();
            if (session is null)
                return "Error: No active debug session. Use DebugStartTest or DebugAttach first.";

            return await session.GetStackTraceAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Gets the current debugger status and position.
    /// </summary>
    [McpServerTool, Description(
        "Get the current debugger status (running/stopped/exited), breakpoint list, " +
        "and current pause position with code context.")]
    public static string DebugStatus()
    {
        var session = DebugSessionManager.GetSession();
        if (session is null)
            return "No active debug session.";

        return session.GetStatus();
    }
}

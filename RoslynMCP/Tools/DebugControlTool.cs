using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

[McpServerToolType]
public static class DebugControlTool
{
    /// <summary>
    /// Continues execution until the next breakpoint or program exit.
    /// </summary>
    [McpServerTool, Description(
        "Continue execution in the active debug session. " +
        "Runs until the next breakpoint is hit or the program exits. " +
        "Returns the current pause location with code context.")]
    public static async Task<string> DebugContinue(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var session = DebugSessionManager.GetSession();
            if (session is null)
                return "Error: No active debug session. Use DebugStartTest or DebugAttach first.";

            return await session.ContinueAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Steps into the next function call.
    /// </summary>
    [McpServerTool, Description(
        "Step into the next function call. Returns the new pause location with code context.")]
    public static async Task<string> DebugStepIn(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var session = DebugSessionManager.GetSession();
            if (session is null)
                return "Error: No active debug session. Use DebugStartTest or DebugAttach first.";

            return await session.StepInAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Steps over the current line.
    /// </summary>
    [McpServerTool, Description(
        "Step over the current line. Returns the new pause location with code context.")]
    public static async Task<string> DebugStepOver(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var session = DebugSessionManager.GetSession();
            if (session is null)
                return "Error: No active debug session. Use DebugStartTest or DebugAttach first.";

            return await session.StepOverAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Steps out of the current function.
    /// </summary>
    [McpServerTool, Description(
        "Step out of the current function. Returns the new pause location with code context.")]
    public static async Task<string> DebugStepOut(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var session = DebugSessionManager.GetSession();
            if (session is null)
                return "Error: No active debug session. Use DebugStartTest or DebugAttach first.";

            return await session.StepOutAsync(cancellationToken);
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
}

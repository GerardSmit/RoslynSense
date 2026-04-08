using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

[McpServerToolType]
public static class DebugStartTool
{
    /// <summary>
    /// Starts a debug session for a .NET test project using netcoredbg.
    /// </summary>
    [McpServerTool, Description(
        "Start debugging a .NET test project. Builds the project, launches the test host, " +
        "and attaches the netcoredbg debugger. Use DebugSetBreakpoint before calling DebugContinue. " +
        "Requires netcoredbg to be installed and on PATH.")]
    public static async Task<string> DebugStartTest(
        [Description("Path to the test project (.csproj) or a source file in the test project.")]
        string projectPath,
        [Description("Optional test filter expression (e.g. 'ClassName.MethodName', 'FullyQualifiedName~MyTest').")]
        string? filter = null,
        [Description("Optional initial breakpoints as 'file:line' pairs, semicolon-separated " +
                     "(e.g. 'MyService.cs:42;MyTest.cs:10').")]
        string? initialBreakpoints = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(projectPath))
                return "Error: 'projectPath' is required.";

            var csprojPath = RunTestsTool.ResolveCsprojPath(projectPath);
            if (csprojPath is null)
                return $"Error: Could not find a .csproj file for '{projectPath}'.";

            DebugSessionManager.DisposeSession();
            var session = DebugSessionManager.CreateSession();
            var breakpoints = ParseBreakpoints(initialBreakpoints);
            return await session.StartTestSessionAsync(csprojPath, filter, breakpoints, cancellationToken);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Attaches the debugger to a running .NET process.
    /// </summary>
    [McpServerTool, Description(
        "Attach the netcoredbg debugger to a running .NET process by PID. " +
        "Use DebugListProcesses to discover .NET processes. " +
        "Requires netcoredbg to be installed and on PATH.")]
    public static async Task<string> DebugAttach(
        [Description("Process ID to attach to.")]
        int pid,
        [Description("Optional initial breakpoints as 'file:line' pairs, semicolon-separated.")]
        string? initialBreakpoints = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            DebugSessionManager.DisposeSession();
            var session = DebugSessionManager.CreateSession();
            var breakpoints = ParseBreakpoints(initialBreakpoints);
            return await session.AttachToProcessAsync(pid, breakpoints, cancellationToken);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Lists running .NET processes that can be attached to.
    /// </summary>
    [McpServerTool, Description(
        "List running .NET processes that can be attached to with DebugAttach.")]
    public static async Task<string> DebugListProcesses(
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await DebuggerService.ListDotNetProcessesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static List<(string file, int line)>? ParseBreakpoints(string? breakpointsStr)
    {
        if (string.IsNullOrWhiteSpace(breakpointsStr))
            return null;

        var result = new List<(string, int)>();
        foreach (var part in breakpointsStr.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var colonIdx = part.LastIndexOf(':');
            if (colonIdx > 0 && int.TryParse(part[(colonIdx + 1)..], out var line))
                result.Add((part[..colonIdx].Trim(), line));
        }
        return result.Count > 0 ? result : null;
    }
}

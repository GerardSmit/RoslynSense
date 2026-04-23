using System.ComponentModel;
using System.Text;
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
        IOutputFormatter fmt,
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

            var normalizedInput = PathHelper.NormalizePath(projectPath);
            var csprojPath = RunTestsTool.ResolveCsprojPath(projectPath);
            if (csprojPath is null)
                return $"Error: Could not find a .csproj file for '{projectPath}'.";

            if (PathHelper.IsSourceFile(normalizedInput))
                filter = PathHelper.BuildSourceFileFilter(normalizedInput, filter);

            if (PathHelper.RequiresMsBuild(csprojPath))
                return "Error: Debugging is not supported for legacy .NET Framework projects. " +
                       "netcoredbg only supports .NET Core 3.0+ and .NET 5+ projects.";

            DebugSessionManager.DisposeSession();
            var session = DebugSessionManager.CreateSession();
            var breakpoints = ParseBreakpoints(initialBreakpoints);
            var result = await session.StartTestSessionAsync(csprojPath, filter, breakpoints, cancellationToken);
            var sb = new StringBuilder(result);
            fmt.AppendHints(sb,
                "Use DebugSetBreakpoint to add breakpoints",
                "Use DebugContinue to start execution");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Attaches the debugger to a running .NET process. When pid is omitted, lists
    /// available .NET processes instead.
    /// </summary>
    [McpServerTool, Description(
        "Attach the netcoredbg debugger to a running .NET process by PID. " +
        "Omit the PID to list available .NET processes. " +
        "Requires netcoredbg to be installed and on PATH.")]
    public static async Task<string> DebugAttach(
        IOutputFormatter fmt,
        [Description("Process ID to attach to. Omit to list available .NET processes.")]
        int pid = 0,
        [Description("Optional initial breakpoints as 'file:line' pairs, semicolon-separated.")]
        string? initialBreakpoints = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (pid <= 0)
                return await DebuggerService.ListDotNetProcessesAsync(cancellationToken);

            DebugSessionManager.DisposeSession();
            var session = DebugSessionManager.CreateSession();
            var breakpoints = ParseBreakpoints(initialBreakpoints);
            var result = await session.AttachToProcessAsync(pid, breakpoints, cancellationToken);
            var sb = new StringBuilder(result);
            fmt.AppendHints(sb,
                "Use DebugSetBreakpoint to add breakpoints",
                "Use DebugContinue to start execution");
            return sb.ToString();
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

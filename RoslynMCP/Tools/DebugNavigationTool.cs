using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

/// <summary>
/// The debugger commands that move execution somewhere other than forwards, plus the module list.
/// </summary>
/// <remarks>
/// Every one of these was already implemented in the ICorDebug engine and unreachable: the engine
/// contract the tools drive did not expose them, so the capability existed and nothing could call
/// it. That is what this file fixes.
/// </remarks>
[McpServerToolType]
[InProcessOnly]
public static class DebugNavigationTool
{
    [McpServerTool, Description(
        "Run to a line without leaving a breakpoint behind — the debugger's 'Run to Cursor'. " +
        "Use this instead of set-breakpoint-then-continue when the line is only interesting once: " +
        "a temporary breakpoint does not stop again on the next lap round a loop.")]
    public static async Task<string> DebugRunToCursor(
        [Description("Path to the source file.")] string filePath,
        [Description("Line to run to.")] int line,
        IOutputFormatter fmt,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (DebugSessionManager.GetSession() is not { } session)
                return "Error: No active debug session. Use DebugStartTest or DebugAttach first.";

            var sb = new StringBuilder(await session.RunToLocationAsync(filePath, line, cancellationToken));
            fmt.AppendHints(sb,
                "Use DebugLocals to see what is in scope there",
                "Use DebugStatus to confirm the position");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description(
        "Move the instruction pointer to another line in the current method — 'Set Next " +
        "Statement'. Re-runs a block after correcting a variable, or skips a call about to " +
        "throw. The runtime refuses moves it cannot make safely. .NET Framework only: netcoredbg " +
        "exposes no way to set the instruction pointer on CoreCLR.")]
    public static async Task<string> DebugSetNextStatement(
        [Description("Path to the source file. Must be the file the current frame is in.")]
        string filePath,
        [Description("Line to move execution to.")] int line,
        IOutputFormatter fmt,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (DebugSessionManager.GetSession() is not { } session)
                return "Error: No active debug session.";

            var sb = new StringBuilder(await session.SetNextStatementAsync(filePath, line, cancellationToken));
            fmt.AppendHints(sb,
                "Nothing between the old and new position has run — locals keep their values",
                "Use DebugStepOver to execute from the new position");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description(
        "List the modules loaded in the debuggee and whether each has symbols. This is the " +
        "actionable answer to 'my breakpoint never binds': without a PDB, no breakpoint in that " +
        "assembly can bind, however correct the file and line are.")]
    public static async Task<string> DebugModules(
        [Description("Show only modules whose name contains this text.")]
        string? filter = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (DebugSessionManager.GetSession() is not { } session)
                return "Error: No active debug session.";

            var modules = await session.GetModulesAsync(cancellationToken);
            if (filter is { Length: > 0 })
            {
                modules = [.. modules.Where(m =>
                    m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))];
            }

            if (modules.Count == 0)
                return "No modules are loaded (or the engine does not report them).";

            var sb = new StringBuilder();
            sb.AppendLine($"# {modules.Count} module(s)");
            sb.AppendLine();
            sb.AppendLine("| Module | Symbols | Path |");
            sb.AppendLine("|--------|---------|------|");
            foreach (var module in modules.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"| {module.Name} | {(module.SymbolsLoaded ? "yes" : "**no**")} | {module.Path} |");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description(
        "Stop debugging but leave the process running. Use this instead of DebugStop for a " +
        "process that was only being inspected — a web app or a service that should not die with " +
        "the debug session.")]
    public static async Task<string> DebugDetach(CancellationToken cancellationToken = default)
    {
        try
        {
            if (DebugSessionManager.GetSession() is not { } session)
                return "Error: No active debug session.";

            string result = await session.DetachAsync(cancellationToken);
            if (!result.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
                DebugSessionManager.DisposeSession();

            return result;
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}

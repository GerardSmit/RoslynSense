using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

[McpServerToolType]
public static class DebugBreakpointTool
{
    /// <summary>
    /// Sets one or more breakpoints in the active debug session.
    /// Supports semicolon-separated 'file:line' pairs for batch mode.
    /// </summary>
    [McpServerTool, Description(
        "Set a breakpoint at a specific file and line in the active debug session. " +
        "Supports multiple breakpoints via semicolon-separated 'file:line' pairs " +
        "(e.g. 'MyService.cs:42;MyTest.cs:10').")]
    public static async Task<string> DebugSetBreakpoint(
        [Description("Path to the source file, or semicolon-separated 'file:line' pairs for batch mode.")] string filePath,
        IOutputFormatter fmt,
        [Description("Line number for the breakpoint (ignored in batch mode).")] int line = 0,
        [Description("Optional condition expression. Breakpoint only triggers when expression is true (e.g. 'x > 5').")]
        string? condition = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var session = DebugSessionManager.GetSession();
            if (session is null)
                return "Error: No active debug session. Use DebugStartTest or DebugAttach first.";

            // Detect batch mode: if filePath contains semicolons, parse as file:line pairs
            if (filePath.Contains(';'))
            {
                var sb = new StringBuilder();
                var pairs = filePath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var pair in pairs)
                {
                    var colonIdx = pair.LastIndexOf(':');
                    if (colonIdx <= 0 || !int.TryParse(pair[(colonIdx + 1)..], out var bpLine))
                    {
                        sb.AppendLine($"Error: Invalid format '{pair}'. Use 'file:line'.");
                        continue;
                    }
                    var bpFile = pair[..colonIdx].Trim();
                    var (msg, _) = await session.SetBreakpointAsync(bpFile, bpLine, condition, cancellationToken);
                    sb.AppendLine(msg);
                }
                fmt.AppendHints(sb,
                    "Use DebugContinue to run to the breakpoint",
                    "Use DebugStatus to see all breakpoints");
                return sb.ToString().TrimEnd();
            }

            {
                var singleResult = (await session.SetBreakpointAsync(filePath, line, condition, cancellationToken)).Message;
                var sbSingle = new StringBuilder(singleResult);
                fmt.AppendHints(sbSingle,
                    "Use DebugContinue to run to the breakpoint",
                    "Use DebugStatus to see all breakpoints");
                return sbSingle.ToString();
            }
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Removes one or more breakpoints from the active debug session.
    /// Supports semicolon-separated IDs for batch removal.
    /// </summary>
    [McpServerTool, Description(
        "Remove a breakpoint by its ID from the active debug session. " +
        "Supports multiple IDs separated by semicolons (e.g. '1;3;5').")]
    public static async Task<string> DebugRemoveBreakpoint(
        [Description("Breakpoint ID to remove, or semicolon-separated IDs for batch removal.")] int breakpointId,
        IOutputFormatter fmt,
        [Description("Alternative: semicolon-separated breakpoint IDs as text (e.g. '1;3;5'). " +
                     "Use this when removing multiple breakpoints at once.")]
        string? breakpointIds = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var session = DebugSessionManager.GetSession();
            if (session is null)
                return "Error: No active debug session. Use DebugStartTest or DebugAttach first.";

            // Batch mode
            if (!string.IsNullOrWhiteSpace(breakpointIds))
            {
                var sb = new StringBuilder();
                var ids = breakpointIds.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var idStr in ids)
                {
                    if (!int.TryParse(idStr, out var id))
                    {
                        sb.AppendLine($"Error: Invalid breakpoint ID '{idStr}'.");
                        continue;
                    }
                    var result = await session.RemoveBreakpointAsync(id, cancellationToken);
                    sb.AppendLine(result);
                }
                fmt.AppendHints(sb,
                    "Use DebugContinue to run to the breakpoint",
                    "Use DebugStatus to see all breakpoints");
                return sb.ToString().TrimEnd();
            }

            {
                var singleResult = await session.RemoveBreakpointAsync(breakpointId, cancellationToken);
                var sbSingle = new StringBuilder(singleResult);
                fmt.AppendHints(sbSingle,
                    "Use DebugContinue to run to the breakpoint",
                    "Use DebugStatus to see all breakpoints");
                return sbSingle.ToString();
            }
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}

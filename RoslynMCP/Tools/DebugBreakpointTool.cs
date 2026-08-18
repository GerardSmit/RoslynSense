using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

[McpServerToolType]
[InProcessOnly]
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
            {
                // Editor route: one command per breakpoint (batch input is split here).
                var pairs = filePath.Contains(';')
                    ? filePath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(pair =>
                        {
                            int colonIdx = pair.LastIndexOf(':');
                            return colonIdx > 0 && int.TryParse(pair[(colonIdx + 1)..], out int bpLine)
                                ? (File: pair[..colonIdx].Trim(), Line: bpLine)
                                : (File: pair, Line: 0);
                        })
                        .ToArray()
                    : [(File: filePath, Line: line)];

                var routedResults = new List<string>();
                foreach (var (bpFile, bpLine) in pairs)
                {
                    var routed = await Services.Debugging.EditorDebugRouter.TryRouteAsync("set_breakpoint",
                        new Dictionary<string, string>
                        {
                            ["file"] = bpFile,
                            ["line"] = bpLine.ToString(),
                            ["condition"] = condition ?? "",
                        }, cancellationToken);
                    if (routed is null)
                        return "Error: No active debug session. Use DebugStartTest or DebugAttach first.";
                    routedResults.Add(routed);
                }
                return string.Join("\n", routedResults);
            }

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
                    var (msg, _) = await session.SetBreakpointAsync(bpFile, bpLine, condition, cancellationToken: cancellationToken);
                    sb.AppendLine(msg);
                }
                fmt.AppendHints(sb,
                    "Use DebugContinue to run to the breakpoint",
                    "Use DebugStatus to see all breakpoints");
                return sb.ToString().TrimEnd();
            }

            {
                var singleResult = (await session.SetBreakpointAsync(filePath, line, condition, cancellationToken: cancellationToken)).Message;
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
            {
                if (Services.Debugging.EditorDebugRouter.ActiveEditorSession() is not null)
                    return "Breakpoint IDs belong to LLM-owned sessions; the user is debugging in the " +
                        "editor, where breakpoints have no IDs. Ask the user to remove the breakpoint, " +
                        "or use DebugSetBreakpoint/DebugContinue to work around it.";
                return "Error: No active debug session. Use DebugStartTest or DebugAttach first.";
            }

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

    [McpServerTool, Description(
        "Break when a value changes rather than when a line is reached — the answer to 'what is " +
        "setting this field to null?'. The expression is evaluated in the current frame, so the " +
        "target must be suspended first. Continue then steps and compares instead of running " +
        "free, which is much slower than a normal breakpoint, and the stop lands on the statement " +
        "after the write. Only changes are detectable, never reads. " +
        "action='clear' drops every watch, so Continue runs at full speed again.")]
    public static async Task<string> DebugWatchValue(
        IOutputFormatter fmt,
        [Description("'watch' (default) adds a watch; 'clear' drops every watched value.")]
        string action = "watch",
        [Description("Expression to watch, e.g. 'order.Total' or '_cache.Count'. Required for action='watch'.")]
        string? expression = null,
        [Description("Only stop when this expression is also true at the moment of the change.")]
        string? condition = null,
        [Description("Hit-count rule for the change, e.g. '>= 3' or '% 5'.")]
        string? hitCondition = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (DebugSessionManager.GetSession() is not Services.Debugging.PublishingDebugBackend session)
                return "Error: No active debug session. Use DebugStartTest or DebugAttach first.";

            if (action.Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                int count = session.DataBreakpoints.Watches.Count;
                await session.SetDataBreakpointsAsync([], cancellationToken);
                return count == 0 ? "No values were being watched." : $"Dropped {count} value watch(es).";
            }

            if (!action.Equals("watch", StringComparison.OrdinalIgnoreCase))
                return $"Error: Unknown action '{action}'. Use: watch, clear.";

            if (expression is not { Length: > 0 })
                return "Error: expression is required for action='watch'.";

            if (session.CurrentFrame is null)
                return "Error: The target is running. Watches read the current frame, so pause or " +
                    "hit a breakpoint first.";

            var specs = session.DataBreakpoints.Watches
                .Where(w => !w.Expression.Equals(expression, StringComparison.Ordinal))
                .Append(new Services.Debugging.DataBreakpointSpec(
                    Services.Debugging.DataBreakpointId.For(expression, 0),
                    expression, "write", condition, hitCondition))
                .ToList();

            var results = await session.SetDataBreakpointsAsync(specs, cancellationToken);
            if (results.FirstOrDefault(r => !r.Verified) is { } failed)
                return $"Error: {failed.Message}";

            var sb = new StringBuilder($"Watching {specs.Count} value(s): " +
                string.Join(", ", specs.Select(s => s.Expression)) + ".");
            fmt.AppendHints(sb,
                "Use DebugContinue to run until one of them changes",
                "Use DebugWatchValue with action='clear' to drop the watches and get normal speed back");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}

using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

[McpServerToolType]
public static class DebugInspectTool
{
    /// <summary>
    /// Evaluates one or more expressions in the current debug frame.
    /// Semicolons separate multiple expressions (returns a table when batched).
    /// </summary>
    [McpServerTool, Description(
        "Evaluate an expression in the current debug context. " +
        "The debugger must be paused at a breakpoint or after stepping. " +
        "Use semicolons to evaluate multiple expressions at once (e.g. 'x;y;list.Count').")]
    public static async Task<string> DebugEvaluate(
        [Description("Expression to evaluate (e.g. 'myVar.Count', 'x + y', 'obj.ToString()'). " +
                     "Separate multiple expressions with semicolons for batch evaluation.")]
        string expression,
        IOutputFormatter fmt,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var session = DebugSessionManager.GetSession();
            if (session is null)
                return "Error: No active debug session. Use DebugStartTest or DebugAttach first.";

            // Auto-detect batch mode by semicolons
            var parts = expression.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
                return "Error: No expressions provided.";

            if (parts.Length == 1)
            {
                var singleResult = await session.EvaluateAsync(parts[0], cancellationToken);
                var sbSingle = new StringBuilder(singleResult);
                fmt.AppendHints(sbSingle,
                    "Use DebugContinue to resume execution",
                    "Use DebugSetBreakpoint to add more breakpoints");
                return sbSingle.ToString();
            }

            // Batch mode: return table
            var sb = new StringBuilder();
            sb.AppendLine("| Expression | Value |");
            sb.AppendLine("|---|---|");

            foreach (var expr in parts)
            {
                string value;
                try
                {
                    value = await session.EvaluateAsync(expr, cancellationToken);
                }
                catch (Exception ex)
                {
                    value = $"Error: {ex.Message}";
                }
                sb.AppendLine($"| {fmt.Escape(expr)} | {fmt.Escape(value)} |");
            }
            fmt.AppendHints(sb,
                "Use DebugContinue to resume execution",
                "Use DebugSetBreakpoint to add more breakpoints");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Gets the current debugger status, optionally including locals and/or stack trace.
    /// </summary>
    [McpServerTool, Description(
        "Get the current debugger status (running/stopped/exited), breakpoint list, " +
        "and current pause position with code context. " +
        "The debugger must be paused at a breakpoint or after stepping.")]
    public static async Task<string> DebugStatus(
        IOutputFormatter fmt,
        [Description("Include local variables in the output. Default: false.")]
        bool includeLocals = false,
        [Description("Include call stack in the output. Default: false.")]
        bool includeStackTrace = false,
        CancellationToken cancellationToken = default)
    {
        var session = DebugSessionManager.GetSession();
        if (session is null)
            return "No active debug session.";

        var sb = new StringBuilder();
        sb.Append(session.GetStatus());

        if (includeLocals)
        {
            try
            {
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine("## Local Variables");
                sb.AppendLine();
                sb.Append(await session.GetLocalsAsync(cancellationToken));
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Error getting locals: {ex.Message}");
            }
        }

        if (includeStackTrace)
        {
            try
            {
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine("## Call Stack");
                sb.AppendLine();
                sb.Append(await session.GetStackTraceAsync(cancellationToken));
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Error getting stack trace: {ex.Message}");
            }
        }
        fmt.AppendHints(sb,
            "Use DebugContinue to resume execution",
            "Use DebugSetBreakpoint to add more breakpoints");

        return sb.ToString();
    }
}

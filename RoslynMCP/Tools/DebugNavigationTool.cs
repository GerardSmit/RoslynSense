using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

/// <summary>
/// The module list and detach — session-level debugger commands that are not execution flow
/// (those live on DebugContinue's action parameter in <see cref="DebugControlTool"/>).
/// </summary>
[McpServerToolType]
[InProcessOnly]
public static class DebugNavigationTool
{
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
            sb.AppendLine($"**{modules.Count} module(s)**");
            sb.AppendLine();
            sb.AppendLine("| Module | Symbols | Source | Path |");
            sb.AppendLine("|--------|---------|--------|------|");
            foreach (var module in modules.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            {
                // The status word when the engine gives one: "not found" and "rejected" have
                // opposite fixes, and a bare "no" hides which of them this is.
                string symbols = module.SymbolStatus is { Length: > 0 } status
                    ? module.SymbolsLoaded ? status : $"**{status}**"
                    : module.SymbolsLoaded ? "yes" : "**no**";
                string origin = module.SymbolOrigin is { Length: > 0 } from
                    ? from
                    : module.SymbolPath;
                sb.AppendLine($"| {module.Name} | {symbols} | {origin} | {module.Path} |");
            }

            // Only for what did not load, and only once per distinct reason: the same stale-PDB
            // sentence repeated for forty modules buries the one that differs.
            var reasons = modules
                .Where(m => !m.SymbolsLoaded && m.SymbolDetail is { Length: > 0 })
                .GroupBy(m => m.SymbolDetail, StringComparer.Ordinal)
                .ToList();
            if (reasons.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("**Why symbols are missing**");
                sb.AppendLine();
                foreach (var reason in reasons)
                {
                    var names = reason.Select(m => m.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
                    sb.AppendLine($"- {string.Join(", ", names)}: {reason.Key}");
                }
            }

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

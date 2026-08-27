using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

[McpServerToolType]
public static class SolutionDiagnosticsTool
{
    /// <summary>
    /// "What is broken here?" in one call. Doing this by looping GetRoslynDiagnostics per file
    /// is slow and quietly incomplete — it can only report on files someone thought to ask about.
    /// </summary>
    [McpServerTool, Description(
        "Compiler diagnostics across every project in the loaded solution, grouped by project. " +
        "Use this to answer 'what is broken?' rather than checking files one at a time.")]
    public static async Task<string> GetSolutionDiagnostics(
        IOutputFormatter fmt,
        [Description("Severity filter: error, warning, info, or all (default: error).")]
        string severityFilter = "error",
        [Description("Maximum diagnostics to list (default: 100).")] int maxResults = 100,
        CancellationToken cancellationToken = default)
    {
        if (!PathHelper.TryParseSeverityFilter(severityFilter, out DiagnosticSeverity? filter))
            return $"Error: Invalid severity filter '{severityFilter}'. Use: error, warning, info, or all.";

        var solution = WorkspaceService.TryGetSessionSolution();
        if (solution is null)
            return "No solution is loaded. Open a file or call OpenSolution first.";

        await using var progress = await ProgressReporter.BeginAsync("Analyzing solution", cancellationToken);

        var byProject = new List<(string Project, List<Diagnostic> Diagnostics)>();
        int total = 0;

        foreach (var project in solution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(project.Name);

            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
                continue;

            var diagnostics = compilation.GetDiagnostics(cancellationToken)
                .Where(d => d.Location.IsInSource && d.Severity != DiagnosticSeverity.Hidden)
                .Where(d => filter is null || d.Severity == filter.Value)
                .OrderByDescending(d => d.Severity)
                .ToList();

            total += diagnostics.Count;
            if (diagnostics.Count > 0)
                byProject.Add((project.Name, diagnostics));
        }

        if (total == 0)
            return filter is null
                ? "No diagnostics in the solution."
                : $"No {severityFilter} diagnostics in the solution.";

        var sb = new StringBuilder();
        fmt.AppendHeader(sb, "Solution diagnostics");
        fmt.AppendField(sb, "Total", total);
        fmt.AppendField(sb, "Projects affected", byProject.Count);

        int shown = 0;
        foreach (var (projectName, diagnostics) in byProject.OrderByDescending(p => p.Diagnostics.Count))
        {
            if (shown >= maxResults)
                break;

            fmt.AppendHeader(sb, $"{projectName} ({diagnostics.Count})", 2);
            foreach (var diagnostic in diagnostics)
            {
                if (shown++ >= maxResults)
                    break;

                var span = diagnostic.Location.GetLineSpan();
                sb.AppendLine(
                    $"- {Path.GetFileName(span.Path)}({span.StartLinePosition.Line + 1}): " +
                    $"{diagnostic.Severity.ToString().ToLowerInvariant()} {diagnostic.Id}: {diagnostic.GetMessage()}");
            }
        }

        if (total > shown)
            sb.AppendLine($"\n_{total - shown} more not shown._");

        return sb.ToString();
    }
}

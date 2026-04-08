using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

/// <summary>
/// Retrieves Roslyn diagnostics for a C# file in a compact, structured markdown table.
/// Complements <see cref="ValidateFileTool"/> with a more token-efficient output format.
/// </summary>
[McpServerToolType]
public static class GetRoslynDiagnosticsTool
{
    /// <summary>
    /// Returns diagnostics for a C# file as a structured markdown table with severity
    /// counts and optional severity filtering.
    /// </summary>
    [McpServerTool, Description(
        "Get Roslyn diagnostics (errors, warnings, info) for a C# file in a compact " +
        "markdown table. For ASPX or Razor files, use ValidateFile instead. " +
        "More structured and token-efficient than ValidateFile. " +
        "Accepts a severity filter to narrow results.")]
    public static async Task<string> GetRoslynDiagnostics(
        [Description("Path to the C# file to diagnose.")] string filePath,
        [Description("Severity filter: error, warning, info, hidden, or all (default: all).")] string severityFilter = "all",
        [Description("Run analyzers (default: true).")] bool runAnalyzers = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TryParseSeverityFilter(severityFilter, out DiagnosticSeverity? filter))
                return $"Error: Invalid severity filter '{severityFilter}'. Use: error, warning, info, hidden, or all.";

            var errors = new StringBuilder();
            var fileCtx = await ToolHelper.ResolveFileAsync(filePath, errors, cancellationToken);
            if (fileCtx is null)
                return errors.ToString();

            if (fileCtx.Document is null)
                return "Error: File not found in project.";

            var diagnostics = await CollectDiagnosticsAsync(
                fileCtx.Document, fileCtx.Project, fileCtx.SystemPath, runAnalyzers, cancellationToken);

            if (filter is not null)
                diagnostics = diagnostics.Where(d => d.Severity == filter.Value).ToList();

            return FormatDiagnostics(diagnostics, fileCtx.SystemPath, fileCtx.Project.FilePath!);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[GetRoslynDiagnostics] Unhandled error: {ex}");
            return $"Error: {ex.Message}";
        }
    }

    private static bool TryParseSeverityFilter(string filter, out DiagnosticSeverity? result)
    {
        switch (filter.ToLowerInvariant())
        {
            case "error": result = DiagnosticSeverity.Error; return true;
            case "warning": result = DiagnosticSeverity.Warning; return true;
            case "info": result = DiagnosticSeverity.Info; return true;
            case "hidden": result = DiagnosticSeverity.Hidden; return true;
            case "all": result = null; return true;
            default: result = null; return false;
        }
    }

    /// <summary>
    /// Collects all diagnostics for the target file from compilation and (optionally)
    /// analyzers, deduplicating by diagnostic ID and source span.
    /// </summary>
    private static async Task<List<Diagnostic>> CollectDiagnosticsAsync(
        Document document, Project project, string filePath, bool runAnalyzers,
        CancellationToken cancellationToken)
    {
        var all = new List<Diagnostic>();

        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation is null)
            return all;

        // Compilation diagnostics include syntax + semantic for all trees;
        // filter to the target file.
        all.AddRange(compilation.GetDiagnostics()
            .Where(d => d.Location.SourceTree is not null &&
                        string.Equals(d.Location.SourceTree.FilePath, filePath,
                            StringComparison.OrdinalIgnoreCase)));

        if (runAnalyzers)
        {
            var analyzerDiags = await AnalyzerService.RunAnalyzersAsync(
                project, compilation, filePath, Console.Error, cancellationToken);
            all.AddRange(analyzerDiags);
        }

        return all
            .GroupBy(d => (d.Id, d.Location.SourceSpan))
            .Select(g => g.First())
            .OrderBy(d => d.Location.SourceSpan.Start)
            .ToList();
    }

    private static string FormatDiagnostics(
        List<Diagnostic> diagnostics, string filePath, string projectPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Diagnostics: {Path.GetFileName(filePath)}");
        sb.AppendLine();

        int errors = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        int warnings = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
        int info = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Info);

        sb.AppendLine(
            $"**Project**: {Path.GetFileName(projectPath)} | " +
            $"**Errors**: {errors} | **Warnings**: {warnings} | **Info**: {info}");
        sb.AppendLine();

        if (diagnostics.Count == 0)
        {
            sb.AppendLine("No diagnostics found.");
            return sb.ToString();
        }

        sb.AppendLine("| Severity | ID | Line:Col | Message |");
        sb.AppendLine("|----------|------|----------|---------|");

        foreach (var d in diagnostics)
        {
            var span = d.Location.GetLineSpan();
            int line = span.StartLinePosition.Line + 1;
            int col = span.StartLinePosition.Character + 1;
            string severity = d.Severity switch
            {
                DiagnosticSeverity.Error => "Error",
                DiagnosticSeverity.Warning => "Warning",
                DiagnosticSeverity.Info => "Info",
                DiagnosticSeverity.Hidden => "Hidden",
                _ => d.Severity.ToString(),
            };

            sb.AppendLine($"| {severity} | {d.Id} | {line}:{col} | {MarkdownHelper.EscapeTableCell(d.GetMessage())} |");
        }

        return sb.ToString();
    }
}

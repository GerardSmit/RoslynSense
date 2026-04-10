using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

/// <summary>
/// Unified diagnostics tool that handles C# files (single-file and project-wide),
/// ASPX/ASCX files, and Razor files. Replaces the former GetRoslynDiagnosticsTool,
/// GetProjectDiagnosticsTool, and ValidateFileTool.
/// </summary>
[McpServerToolType]
public static class GetRoslynDiagnosticsTool
{
    [McpServerTool, Description(
        "Get Roslyn diagnostics (errors, warnings) for a C# file, ASPX/ASCX file, " +
        "Razor file, or entire project. Pass a source file path for file-level diagnostics, " +
        "or a .csproj path for project-wide diagnostics. " +
        "Also supports ASPX/ASCX and Razor (.razor/.cshtml) files. " +
        "Supports multiple files separated by semicolons. " +
        "Accepts a severity filter to narrow results.")]
    public static async Task<string> GetRoslynDiagnostics(
        [Description("Path to the C# file, ASPX/ASCX file, Razor file, or .csproj project. " +
                     "Separate multiple paths with semicolons.")] string filePath,
        [Description("Severity filter: error, warning, info, hidden, or all (default: all).")] string severityFilter = "all",
        [Description("Run analyzers (default: true).")] bool runAnalyzers = true,
        [Description("Maximum number of diagnostics to return. Default: 50.")] int maxResults = 50,
        IEnumerable<IDiagnosticsHandler>? handlers = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return "Error: filePath cannot be empty.";

            var paths = filePath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (paths.Length == 1)
                return await GetSingleFileDiagnostics(paths[0], severityFilter, runAnalyzers, maxResults, handlers, cancellationToken);

            // Multi-file mode
            var sb = new StringBuilder();
            foreach (var path in paths)
            {
                if (sb.Length > 0)
                    sb.AppendLine();

                sb.Append(await GetSingleFileDiagnostics(path, severityFilter, runAnalyzers, maxResults, handlers, cancellationToken));
            }
            return sb.ToString();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[GetRoslynDiagnostics] Unhandled error: {ex}");
            return $"Error: {ex.Message}";
        }
    }

    private static async Task<string> GetSingleFileDiagnostics(
        string filePath, string severityFilter, bool runAnalyzers, int maxResults,
        IEnumerable<IDiagnosticsHandler>? handlers, CancellationToken cancellationToken)
    {
        string systemPath = PathHelper.NormalizePath(filePath);

        // Delegate to registered handlers for non-C# file types
        if (handlers is not null)
        {
            foreach (var handler in handlers)
            {
                if (handler.CanHandle(systemPath))
                    return await handler.ValidateAsync(systemPath, cancellationToken);
            }
        }

        // Project-wide diagnostics (.csproj path)
        if (systemPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return await GetProjectDiagnosticsAsync(systemPath, severityFilter, maxResults, cancellationToken);

        // Single C# file diagnostics
        return await GetFileDiagnosticsAsync(systemPath, severityFilter, runAnalyzers, maxResults, cancellationToken);
    }

    /// <summary>
    /// File-level diagnostics for a single C# file.
    /// </summary>
    private static async Task<string> GetFileDiagnosticsAsync(
        string systemPath, string severityFilter, bool runAnalyzers, int maxResults,
        CancellationToken cancellationToken)
    {
        if (!PathHelper.TryParseSeverityFilter(severityFilter, out DiagnosticSeverity? filter))
            return $"Error: Invalid severity filter '{severityFilter}'. Use: error, warning, info, hidden, or all.";

        var errors = new StringBuilder();
        var fileCtx = await ToolHelper.ResolveFileAsync(systemPath, errors, cancellationToken);
        if (fileCtx is null)
            return errors.ToString();

        if (fileCtx.Document is null)
            return "Error: File not found in project.";

        var diagnostics = await CollectFileDiagnosticsAsync(
            fileCtx.Document, fileCtx.Project, fileCtx.SystemPath, runAnalyzers, cancellationToken);

        if (filter is not null)
            diagnostics = diagnostics.Where(d => d.Severity == filter.Value).ToList();

        return FormatFileDiagnostics(diagnostics, fileCtx.SystemPath, fileCtx.Project.FilePath!, maxResults);
    }

    /// <summary>
    /// Project-wide diagnostics for all files in a .csproj.
    /// </summary>
    private static async Task<string> GetProjectDiagnosticsAsync(
        string csprojPath, string severityFilter, int maxResults,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(csprojPath))
            return $"Error: Project file '{csprojPath}' not found.";

        if (!PathHelper.TryParseSeverityFilter(severityFilter, out DiagnosticSeverity? filter))
            return $"Error: Invalid severity filter '{severityFilter}'. Use: error, warning, info, or all.";

        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
            csprojPath, cancellationToken: cancellationToken);

        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation is null)
            return "Error: Unable to produce compilation for project.";

        var diagnostics = compilation.GetDiagnostics()
            .Where(d => d.Location.IsInSource)
            .Where(d => d.Severity != DiagnosticSeverity.Hidden)
            .ToList();

        if (filter is not null)
            diagnostics = diagnostics.Where(d => d.Severity == filter.Value).ToList();

        return FormatProjectDiagnostics(diagnostics, project, csprojPath, maxResults);
    }

    /// <summary>
    /// Collects all diagnostics for a single file from compilation and (optionally)
    /// analyzers, deduplicating by diagnostic ID and source span.
    /// </summary>
    private static async Task<List<Diagnostic>> CollectFileDiagnosticsAsync(
        Document document, Project project, string filePath, bool runAnalyzers,
        CancellationToken cancellationToken)
    {
        var all = new List<Diagnostic>();

        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation is null)
            return all;

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

    private static string FormatFileDiagnostics(
        List<Diagnostic> diagnostics, string filePath, string projectPath, int maxResults)
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

        var ordered = diagnostics.Take(maxResults).ToList();

        sb.AppendLine("| Severity | ID | Line:Col | Message |");
        sb.AppendLine("|----------|------|----------|---------|");

        foreach (var d in ordered)
        {
            var span = d.Location.GetLineSpan();
            int line = span.StartLinePosition.Line + 1;
            int col = span.StartLinePosition.Character + 1;
            string severity = FormatSeverity(d.Severity);
            sb.AppendLine($"| {severity} | {d.Id} | {line}:{col} | {MarkdownFormatter.EscapeTableCell(d.GetMessage())} |");
        }

        if (diagnostics.Count > maxResults)
        {
            sb.AppendLine();
            sb.AppendLine($"_Showing first {maxResults} of {diagnostics.Count} diagnostics. Use `maxResults` to see more._");
        }

        return sb.ToString();
    }

    private static string FormatProjectDiagnostics(
        List<Diagnostic> diagnostics, Project project, string projectPath, int maxResults)
    {
        var sb = new StringBuilder();
        string? projectDir = Path.GetDirectoryName(projectPath);

        sb.AppendLine($"# Project Diagnostics: {project.Name}");
        sb.AppendLine();

        int errors = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        int warnings = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
        int info = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Info);

        sb.AppendLine($"**Errors**: {errors} | **Warnings**: {warnings} | **Info**: {info}");
        sb.AppendLine();

        if (diagnostics.Count == 0)
        {
            sb.AppendLine("No diagnostics found. Project compiles cleanly.");
            return sb.ToString();
        }

        var byFile = diagnostics
            .GroupBy(d => d.Location.SourceTree?.FilePath ?? "unknown")
            .OrderByDescending(g => g.Count(d => d.Severity == DiagnosticSeverity.Error))
            .ThenByDescending(g => g.Count())
            .ToList();

        sb.AppendLine($"**Files with issues**: {byFile.Count}");
        sb.AppendLine();

        var ordered = diagnostics
            .OrderBy(d => d.Severity)
            .ThenBy(d => d.Location.SourceTree?.FilePath)
            .ThenBy(d => d.Location.SourceSpan.Start)
            .Take(maxResults)
            .ToList();

        sb.AppendLine("| Severity | ID | File | Line | Message |");
        sb.AppendLine("|----------|------|------|------|---------|");

        foreach (var d in ordered)
        {
            var span = d.Location.GetLineSpan();
            int line = span.StartLinePosition.Line + 1;
            string severity = FormatSeverity(d.Severity);
            string file = projectDir is not null
                ? Path.GetRelativePath(projectDir, span.Path)
                : Path.GetFileName(span.Path);

            sb.AppendLine(
                $"| {severity} | {d.Id} | {MarkdownFormatter.EscapeTableCell(file)} | {line} | {MarkdownFormatter.EscapeTableCell(d.GetMessage())} |");
        }

        if (diagnostics.Count > maxResults)
        {
            sb.AppendLine();
            sb.AppendLine($"_Showing first {maxResults} of {diagnostics.Count} diagnostics. Use `maxResults` to see more._");
        }

        return sb.ToString();
    }

    internal static string FormatSeverity(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Error => "Error",
        DiagnosticSeverity.Warning => "Warning",
        DiagnosticSeverity.Info => "Info",
        DiagnosticSeverity.Hidden => "Hidden",
        _ => severity.ToString(),
    };
}

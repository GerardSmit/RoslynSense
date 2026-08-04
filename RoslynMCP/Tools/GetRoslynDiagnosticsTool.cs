using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynMCP.Services;
using RoslynMCP.Languages;

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
        IOutputFormatter fmt,
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
                return await GetSingleFileDiagnostics(paths[0], severityFilter, runAnalyzers, maxResults, fmt, handlers, cancellationToken);

            // Multi-file mode
            var sb = new StringBuilder();
            foreach (var path in paths)
            {
                if (sb.Length > 0)
                    sb.AppendLine();

                sb.Append(await GetSingleFileDiagnostics(path, severityFilter, runAnalyzers, maxResults, fmt, handlers, cancellationToken));
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
        IOutputFormatter fmt, IEnumerable<IDiagnosticsHandler>? handlers, CancellationToken cancellationToken)
    {
        string systemPath = PathHelper.NormalizePath(filePath);

        // Delegate to registered handlers for non-C# file types
        if (handlers is not null)
        {
            foreach (var handler in handlers)
            {
                if (handler.CanHandle(systemPath))
                    return await handler.ValidateAsync(systemPath, fmt, cancellationToken);
            }
        }

        // Project-wide diagnostics (.csproj path)
        if (systemPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return await GetProjectDiagnosticsAsync(systemPath, severityFilter, runAnalyzers, maxResults, fmt, cancellationToken);

        // Single C# file diagnostics
        return await GetFileDiagnosticsAsync(systemPath, severityFilter, runAnalyzers, maxResults, fmt, cancellationToken);
    }

    /// <summary>
    /// File-level diagnostics for a single C# file.
    /// </summary>
    private static async Task<string> GetFileDiagnosticsAsync(
        string systemPath, string severityFilter, bool runAnalyzers, int maxResults,
        IOutputFormatter fmt, CancellationToken cancellationToken)
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

        return FormatFileDiagnostics(diagnostics, fileCtx.SystemPath, fileCtx.Project.FilePath!, maxResults, fmt);
    }

    /// <summary>
    /// Project-wide diagnostics for all files in a .csproj.
    /// </summary>
    private static async Task<string> GetProjectDiagnosticsAsync(
        string csprojPath, string severityFilter, bool runAnalyzers, int maxResults,
        IOutputFormatter fmt, CancellationToken cancellationToken)
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

        var compilerDiagnostics = compilation.GetDiagnostics()
            .Where(d => d.Location.IsInSource)
            .Where(d => d.Severity != DiagnosticSeverity.Hidden);

        // Project scope ran compiler diagnostics only while file scope ran analyzers, so the
        // same rule reported or vanished depending on how it was asked for.
        var analyzerDiagnostics = runAnalyzers
            ? await AnalyzerService.RunAnalyzersAsync(
                project, compilation, filePath: null, Console.Error, cancellationToken)
            : Array.Empty<Diagnostic>();

        var diagnostics = compilerDiagnostics
            .Concat(analyzerDiagnostics.Where(d => d.Severity != DiagnosticSeverity.Hidden))
            .GroupBy(d => (d.Id, d.Location.SourceSpan, d.Location.SourceTree?.FilePath))
            .Select(g => g.First())
            .ToList();

        if (filter is not null)
            diagnostics = diagnostics.Where(d => d.Severity == filter.Value).ToList();

        return FormatProjectDiagnostics(diagnostics, project, csprojPath, maxResults, fmt);
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
        List<Diagnostic> diagnostics, string filePath, string projectPath, int maxResults, IOutputFormatter fmt)
    {
        var sb = new StringBuilder();
        fmt.AppendHeader(sb, $"Diagnostics: {Path.GetFileName(filePath)}");

        int errors = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        int warnings = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
        int info = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Info);

        fmt.AppendField(sb, "Project", Path.GetFileName(projectPath));
        fmt.AppendField(sb, "Errors", errors);
        fmt.AppendField(sb, "Warnings", warnings);
        fmt.AppendField(sb, "Info", info);

        if (diagnostics.Count == 0)
        {
            fmt.AppendEmpty(sb, "No diagnostics found.");
            return sb.ToString();
        }

        var ordered = diagnostics.Take(maxResults).ToList();
        fmt.BeginTable(sb, "Diagnostics", ["Severity", "ID", "Line:Col", "Message"], diagnostics.Count);
        foreach (var d in ordered)
        {
            var span = d.Location.GetLineSpan();
            int line = span.StartLinePosition.Line + 1;
            int col = span.StartLinePosition.Character + 1;
            fmt.AddRow(sb, FormatSeverity(d.Severity), d.Id, $"{line}:{col}", d.GetMessage());
        }
        fmt.EndTable(sb);
        fmt.AppendTruncation(sb, ordered.Count, diagnostics.Count);

        return sb.ToString();
    }

    private static string FormatProjectDiagnostics(
        List<Diagnostic> diagnostics, Project project, string projectPath, int maxResults, IOutputFormatter fmt)
    {
        var sb = new StringBuilder();
        string? projectDir = Path.GetDirectoryName(projectPath);

        fmt.AppendHeader(sb, $"Project Diagnostics: {project.Name}");

        int errors = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        int warnings = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
        int info = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Info);

        fmt.AppendField(sb, "Errors", errors);
        fmt.AppendField(sb, "Warnings", warnings);
        fmt.AppendField(sb, "Info", info);

        if (diagnostics.Count == 0)
        {
            fmt.AppendEmpty(sb, "No diagnostics found. Project compiles cleanly.");
            return sb.ToString();
        }

        var byFile = diagnostics
            .GroupBy(d => d.Location.SourceTree?.FilePath ?? "unknown")
            .OrderByDescending(g => g.Count(d => d.Severity == DiagnosticSeverity.Error))
            .ThenByDescending(g => g.Count())
            .ToList();

        fmt.AppendField(sb, "Files with issues", byFile.Count);

        var ordered = diagnostics
            .OrderBy(d => d.Severity)
            .ThenBy(d => d.Location.SourceTree?.FilePath)
            .ThenBy(d => d.Location.SourceSpan.Start)
            .Take(maxResults)
            .ToList();

        fmt.BeginTable(sb, "Diagnostics", ["Severity", "ID", "File", "Line", "Message"], diagnostics.Count);
        foreach (var d in ordered)
        {
            var span = d.Location.GetLineSpan();
            int line = span.StartLinePosition.Line + 1;
            string file = projectDir is not null
                ? Path.GetRelativePath(projectDir, span.Path)
                : Path.GetFileName(span.Path);
            fmt.BeginRow(sb);
            fmt.WriteCell(sb, FormatSeverity(d.Severity));
            fmt.WriteCell(sb, d.Id);
            fmt.WriteCell(sb, file);
            fmt.WriteCell(sb, line);
            fmt.WriteCell(sb, d.GetMessage());
            fmt.EndRow(sb);
        }
        fmt.EndTable(sb);
        fmt.AppendTruncation(sb, ordered.Count, diagnostics.Count);

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

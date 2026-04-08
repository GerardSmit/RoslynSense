using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

[McpServerToolType]
public static class ValidateFileTool
{
    [McpServerTool, Description("Validates a C# file using Roslyn and runs code analyzers. Also supports ASPX/ASCX and Razor (.razor/.cshtml) files. Accepts either a relative or absolute file path.")]
    public static async Task<string> ValidateFile(
        [Description("The path to the C#, ASPX, or Razor file to validate")] string filePath,
        [Description("Run analyzers (default: true)")] bool runAnalyzers = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return "Error: File path cannot be empty.";

            string systemPath = PathHelper.NormalizePath(filePath);

            if (!File.Exists(systemPath))
                return $"Error: File {systemPath} does not exist.";

            // ASPX files: parse with WebFormsCore.Parser and show outline + diagnostics
            if (AspxSourceMappingService.IsAspxFile(systemPath))
                return await ValidateAspxFileAsync(systemPath, cancellationToken);

            // Razor files: find the project, build source map, report mapped diagnostics
            if (RazorSourceMappingService.IsRazorFile(systemPath))
                return await ValidateRazorFileAsync(systemPath, cancellationToken);

            string? projectPath = await WorkspaceService.FindContainingProjectAsync(systemPath, cancellationToken);
            if (string.IsNullOrEmpty(projectPath))
                return "Error: Couldn't find a project containing this file.";

            Console.Error.WriteLine($"[ValidateFile] Validating '{systemPath}' in project '{projectPath}'");

            var outputWriter = new StringWriter();
            await ValidateFileInProjectContextAsync(
                systemPath, projectPath, outputWriter, runAnalyzers, cancellationToken);
            return outputWriter.ToString();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ValidateFile] Unhandled error: {ex}");
            return $"Error processing file: {ex.Message}";
        }
    }

    /// <summary>
    /// Validates a C# file within its project context, reporting syntax, semantic,
    /// compilation, and (optionally) analyzer diagnostics to <paramref name="writer"/>.
    /// </summary>
    internal static async Task ValidateFileInProjectContextAsync(
        string filePath, string projectPath, TextWriter? writer = null, bool runAnalyzers = true,
        CancellationToken cancellationToken = default)
    {
        writer ??= Console.Out;

        try
        {
            var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
                projectPath, targetFilePath: filePath, diagnosticWriter: Console.Error,
                cancellationToken: cancellationToken);

            writer.WriteLine($"Loading project: {projectPath}");
            writer.WriteLine($"Project loaded successfully: {project.Name}");

            var document = WorkspaceService.FindDocumentInProject(project, filePath);

            if (document == null)
            {
                writer.WriteLine("Error: File not found in the project documents.");
                writer.WriteLine("All project documents:");
                foreach (var doc in project.Documents)
                {
                    writer.WriteLine($"  - {doc.FilePath}");
                }

                return;
            }

            writer.WriteLine($"Document found: {document.Name}");

            // Syntax diagnostics
            var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
            if (syntaxTree == null)
            {
                writer.WriteLine("Error: Unable to obtain syntax tree for document.");
                return;
            }

            var syntaxDiagnostics = syntaxTree.GetDiagnostics();

            if (syntaxDiagnostics.Any())
            {
                writer.WriteLine("Syntax errors found:");
                foreach (var diagnostic in syntaxDiagnostics)
                {
                    var location = diagnostic.Location.GetLineSpan();
                    writer.WriteLine($"Line {location.StartLinePosition.Line + 1}: {diagnostic.GetMessage()}");
                }
            }
            else
            {
                writer.WriteLine("No syntax errors found.");
            }

            // Semantic diagnostics
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (semanticModel == null)
            {
                writer.WriteLine("Error: Unable to obtain semantic model for document.");
                return;
            }

            var semanticDiagnostics = semanticModel.GetDiagnostics();

            if (semanticDiagnostics.Any())
            {
                writer.WriteLine("\nSemantic errors found:");
                foreach (var diagnostic in semanticDiagnostics)
                {
                    var location = diagnostic.Location.GetLineSpan();
                    writer.WriteLine($"Line {location.StartLinePosition.Line + 1}: {diagnostic.GetMessage()}");
                }
            }
            else
            {
                writer.WriteLine("No semantic errors found.");
            }

            // Compilation diagnostics
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null)
            {
                writer.WriteLine("Error: Unable to produce compilation for project.");
                return;
            }

            var compilationDiagnostics = compilation.GetDiagnostics()
                .Where(d => d.Location.SourceTree != null &&
                            string.Equals(d.Location.SourceTree.FilePath, filePath,
                                StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Analyzer diagnostics
            IEnumerable<Diagnostic> analyzerDiagnostics = Array.Empty<Diagnostic>();
            if (runAnalyzers)
            {
                analyzerDiagnostics = await AnalyzerService.RunAnalyzersAsync(
                    project, compilation, filePath, writer, cancellationToken);
            }

            var allDiagnostics = compilationDiagnostics.Concat(analyzerDiagnostics);

            if (allDiagnostics.Any())
            {
                writer.WriteLine("\nCompilation and analyzer diagnostics:");
                foreach (var diagnostic in allDiagnostics.OrderBy(d => d.Severity))
                {
                    var location = diagnostic.Location.GetLineSpan();
                    var severity = diagnostic.Severity.ToString();
                    writer.WriteLine(
                        $"[{severity}] Line {location.StartLinePosition.Line + 1}: {diagnostic.Id} - {diagnostic.GetMessage()}");
                }
            }
            else
            {
                writer.WriteLine("File compiles successfully in project context with no analyzer warnings.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            writer.WriteLine($"Error validating file: {ex.Message}");
            if (ex.InnerException != null)
            {
                writer.WriteLine($"Inner exception: {ex.InnerException.Message}");
            }
        }
    }

    /// <summary>
    /// Validates an ASPX/ASCX file by parsing it with WebFormsCore.Parser and reporting
    /// an outline of directives, controls, expressions, and any parse errors.
    /// Reads web.config for globally registered tag prefixes.
    /// </summary>
    private static async Task<string> ValidateAspxFileAsync(
        string filePath, CancellationToken cancellationToken)
    {
        string? projectPath = await FindProjectForNonCSharpFileAsync(filePath, cancellationToken);
        if (string.IsNullOrEmpty(projectPath))
            return "Error: Couldn't find a project containing this ASPX file.";

        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
            projectPath, cancellationToken: cancellationToken);
        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation is null)
            return "Error: Unable to produce compilation for project.";

        var projectDir = Path.GetDirectoryName(projectPath);
        var webConfigNamespaces = projectDir is not null
            ? AspxSourceMappingService.LoadWebConfigNamespaces(projectDir)
            : default;

        var text = await File.ReadAllTextAsync(filePath, cancellationToken);
        var result = AspxSourceMappingService.Parse(filePath, text, compilation,
            namespaces: webConfigNamespaces.IsDefaultOrEmpty ? null : webConfigNamespaces,
            rootDirectory: projectDir);
        return AspxSourceMappingService.FormatOutline(result);
    }

    /// <summary>
    /// Validates a Razor file by finding its source-generated counterpart and reporting
    /// diagnostics mapped back to the original .razor/.cshtml source.
    /// </summary>
    private static async Task<string> ValidateRazorFileAsync(
        string filePath, CancellationToken cancellationToken)
    {
        string? projectPath = await FindProjectForNonCSharpFileAsync(filePath, cancellationToken);
        if (string.IsNullOrEmpty(projectPath))
            return "Error: Couldn't find a project containing this Razor file.";

        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
            projectPath, cancellationToken: cancellationToken);

        var sourceMap = await ProjectIndexCacheService.GetRazorSourceMapAsync(project, cancellationToken);

        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation is null)
            return "Error: Unable to produce compilation for project.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Razor Validation: {Path.GetFileName(filePath)}");
        sb.AppendLine();

        var allDiagnostics = compilation.GetDiagnostics();
        var mappedDiags = new List<RazorMappedDiagnostic>();

        foreach (var diag in allDiagnostics)
        {
            var mapped = RazorSourceMappingService.MapDiagnostic(sourceMap, diag);
            if (mapped.MappedLocation is not null &&
                string.Equals(
                    Path.GetFullPath(mapped.MappedLocation.RazorFilePath),
                    Path.GetFullPath(filePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                mappedDiags.Add(mapped);
            }
        }

        if (mappedDiags.Count == 0)
        {
            sb.AppendLine("No diagnostics found for this Razor file.");
        }
        else
        {
            int errors = mappedDiags.Count(d => d.Diagnostic.Severity == DiagnosticSeverity.Error);
            int warnings = mappedDiags.Count(d => d.Diagnostic.Severity == DiagnosticSeverity.Warning);
            sb.AppendLine($"**Errors**: {errors} | **Warnings**: {warnings}");
            sb.AppendLine();
            sb.AppendLine("| Severity | ID | Razor Line | Message |");
            sb.AppendLine("|----------|------|------------|---------|");

            foreach (var mapped in mappedDiags.OrderBy(d => d.MappedLocation!.Line))
            {
                var d = mapped.Diagnostic;
                string severity = d.Severity switch
                {
                    DiagnosticSeverity.Error => "Error",
                    DiagnosticSeverity.Warning => "Warning",
                    DiagnosticSeverity.Info => "Info",
                    _ => d.Severity.ToString(),
                };
                sb.AppendLine(
                    $"| {severity} | {d.Id} | {mapped.MappedLocation!.Line} | {MarkdownHelper.EscapeTableCell(d.GetMessage())} |");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Finds the .csproj that contains a non-C# file (ASPX, Razor, etc.) by first
    /// trying the standard document lookup, then walking up the directory tree.
    /// </summary>
    private static async Task<string?> FindProjectForNonCSharpFileAsync(
        string filePath, CancellationToken cancellationToken)
    {
        // Try the standard lookup first (works when a project is already loaded)
        string? projectPath = await WorkspaceService.FindContainingProjectAsync(filePath, cancellationToken);
        if (!string.IsNullOrEmpty(projectPath))
            return projectPath;

        // Walk up the directory tree to find the nearest .csproj
        var dir = new DirectoryInfo(Path.GetDirectoryName(filePath)!);
        while (dir is not null)
        {
            var csproj = dir.GetFiles("*.csproj").FirstOrDefault();
            if (csproj is not null)
                return csproj.FullName;
            dir = dir.Parent;
        }

        return null;
    }
}

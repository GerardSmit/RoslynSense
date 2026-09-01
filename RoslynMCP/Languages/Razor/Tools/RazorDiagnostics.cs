using System.Text;
using Microsoft.CodeAnalysis;
using RoslynMCP.Services;
using RoslynMCP.Tools;

namespace RoslynMCP.Languages.Razor.Tools;

/// <summary>
/// Validates Razor files (.razor/.cshtml) by mapping compilation diagnostics
/// from generated C# back to Razor source lines.
/// </summary>
internal class RazorDiagnostics : IDiagnosticsHandler
{
    public bool CanHandle(string filePath) => RazorSourceMappingService.IsRazorFile(filePath);

    public async Task<string> ValidateAsync(
        string filePath, IOutputFormatter fmt, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
            return $"Error: File {filePath} does not exist.";

        string? projectPath = await NonCSharpProjectFinder.FindProjectAsync(filePath, cancellationToken);
        if (string.IsNullOrEmpty(projectPath))
            return "Error: Couldn't find a project containing this Razor file.";

        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
            projectPath, cancellationToken: cancellationToken);

        var sourceMap = await ProjectIndexCacheService.GetRazorSourceMapAsync(project, cancellationToken);

        var sb = new StringBuilder();
        sb.AppendLine($"**Razor Validation: {Path.GetFileName(filePath)}**");
        sb.AppendLine();

        // Bind only this file's generated documents instead of the whole compilation: the map
        // already knows which generated docs carry #line directives pointing at this file, and
        // the version-keyed cache is shared with the LSP session in the same process.
        var mappedDiags = new List<RazorMappedDiagnostic>();
        foreach (var genDoc in GeneratedDocumentsFor(sourceMap, filePath))
        {
            var result = await Lsp.CompilerDiagnosticCache.GetOrComputeAsync(genDoc, cancellationToken);
            foreach (var diag in result.Compiler)
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
        }

        if (mappedDiags.Count == 0)
        {
            fmt.AppendEmpty(sb, "No diagnostics found for this Razor file.");
        }
        else
        {
            int errors = mappedDiags.Count(d => d.Diagnostic.Severity == DiagnosticSeverity.Error);
            int warnings = mappedDiags.Count(d => d.Diagnostic.Severity == DiagnosticSeverity.Warning);
            fmt.AppendField(sb, "Errors", errors);
            fmt.AppendField(sb, "Warnings", warnings);

            fmt.BeginTable(sb, "Diagnostics", ["Severity", "ID", "Razor Line", "Message"], mappedDiags.Count);
            foreach (var mapped in mappedDiags.OrderBy(d => d.MappedLocation!.Line))
            {
                var d = mapped.Diagnostic;
                fmt.BeginRow(sb);
                fmt.WriteCell(sb, GetRoslynDiagnosticsTool.FormatSeverity(d.Severity));
                fmt.WriteCell(sb, d.Id);
                fmt.WriteCell(sb, mapped.MappedLocation!.Line);
                fmt.WriteCell(sb, d.GetMessage());
                fmt.EndRow(sb);
            }
            fmt.EndTable(sb);
        }

        return sb.ToString();
    }

    /// <summary>The generated documents whose #line directives point at <paramref name="filePath"/>.
    /// The map's keys are the paths as the directives wrote them, so a direct hit is tried first
    /// and a normalized comparison second.</summary>
    private static IEnumerable<SourceGeneratedDocument> GeneratedDocumentsFor(
        RazorSourceMap sourceMap, string filePath)
    {
        if (sourceMap.RazorToGeneratedDocuments.TryGetValue(filePath, out var docs))
            return docs;

        string fullPath = Path.GetFullPath(filePath);
        return sourceMap.RazorToGeneratedDocuments
            .Where(kv => string.Equals(Path.GetFullPath(kv.Key), fullPath, StringComparison.OrdinalIgnoreCase))
            .SelectMany(kv => kv.Value);
    }
}

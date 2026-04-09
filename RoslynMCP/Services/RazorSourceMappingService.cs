using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMCP.Services;

/// <summary>
/// Maps Razor-generated C# source locations back to original .razor/.cshtml files
/// by parsing <c>#line</c> directives in source-generated documents.
/// </summary>
internal static partial class RazorSourceMappingService
{
    private static readonly string[] s_razorExtensions = [".razor", ".cshtml"];

    /// <summary>
    /// Returns <c>true</c> when the file has a Razor extension (.razor or .cshtml).
    /// </summary>
    public static bool IsRazorFile(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return s_razorExtensions.Any(e => string.Equals(ext, e, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Builds a source map for a project by inspecting all source-generated documents
    /// and parsing their <c>#line</c> directives.
    /// </summary>
    public static async Task<RazorSourceMap> BuildSourceMapAsync(
        Project project, CancellationToken cancellationToken = default)
    {
        var mappings = new List<RazorLineMapping>();
        var razorToGenerated = new Dictionary<string, List<SourceGeneratedDocument>>(StringComparer.OrdinalIgnoreCase);

        var generatedDocs = await project.GetSourceGeneratedDocumentsAsync(cancellationToken);

        foreach (var doc in generatedDocs)
        {
            if (!IsRazorGeneratedDocument(doc))
                continue;

            var text = await doc.GetTextAsync(cancellationToken);
            var lineMappings = ParseLineDirectives(doc.FilePath ?? doc.Name, text);

            foreach (var mapping in lineMappings)
            {
                mappings.Add(mapping);

                if (!razorToGenerated.TryGetValue(mapping.RazorFilePath, out var list))
                {
                    list = [];
                    razorToGenerated[mapping.RazorFilePath] = list;
                }

                if (!list.Contains(doc))
                    list.Add(doc);
            }
        }

        return new RazorSourceMap(mappings, razorToGenerated);
    }

    /// <summary>
    /// Maps a location in generated C# code back to the original .razor/.cshtml file.
    /// Returns <c>null</c> if the location is not in a Razor-mapped region.
    /// </summary>
    /// <summary>
    /// Maps a 1-indexed line in generated C# code back to the original .razor/.cshtml file.
    /// Returns <c>null</c> if the location is not in a Razor-mapped region.
    /// </summary>
    public static RazorMappedLocation? MapGeneratedToRazor(
        RazorSourceMap sourceMap, string generatedFilePath, int generatedLine)
    {
        // Find the most specific mapping for this generated line.
        // All line numbers are 1-indexed. GeneratedEndLine is an exclusive upper bound.
        RazorLineMapping? bestMapping = null;

        foreach (var mapping in sourceMap.Mappings)
        {
            if (!string.Equals(mapping.GeneratedFilePath, generatedFilePath, StringComparison.OrdinalIgnoreCase))
                continue;

            if (generatedLine < mapping.GeneratedStartLine)
                continue;

            if (mapping.GeneratedEndLine.HasValue && generatedLine >= mapping.GeneratedEndLine.Value)
                continue;

            if (bestMapping is null || mapping.GeneratedStartLine > bestMapping.GeneratedStartLine)
                bestMapping = mapping;
        }

        if (bestMapping is null)
            return null;

        int offset = generatedLine - bestMapping.GeneratedStartLine;
        int razorLine = bestMapping.RazorLine + offset;

        return new RazorMappedLocation(bestMapping.RazorFilePath, razorLine);
    }

    /// <summary>
    /// Maps a 1-indexed line in a Razor source file to a location in generated C#.
    /// Returns the generated document and 1-indexed line number, or <c>null</c> if no mapping exists.
    /// </summary>
    public static RazorGeneratedLocation? MapRazorToGenerated(
        RazorSourceMap sourceMap, string razorFilePath, int razorLine)
    {
        RazorLineMapping? bestMapping = null;

        foreach (var mapping in sourceMap.Mappings)
        {
            if (!string.Equals(mapping.RazorFilePath, razorFilePath, StringComparison.OrdinalIgnoreCase))
                continue;

            // Check if the razor line falls within this mapping's razor range
            int mappingRazorEndLine = mapping.RazorLine +
                (mapping.GeneratedEndLine.HasValue
                    ? mapping.GeneratedEndLine.Value - mapping.GeneratedStartLine
                    : 0);

            if (razorLine < mapping.RazorLine || razorLine > mappingRazorEndLine)
                continue;

            // Prefer the most specific (narrowest range) mapping
            if (bestMapping is null ||
                mapping.GeneratedStartLine > bestMapping.GeneratedStartLine)
                bestMapping = mapping;
        }

        if (bestMapping is null)
            return null;

        int offset = razorLine - bestMapping.RazorLine;
        int generatedLine = bestMapping.GeneratedStartLine + offset;

        return new RazorGeneratedLocation(bestMapping.GeneratedFilePath, generatedLine);
    }

    /// <summary>
    /// Maps a diagnostic's location back to Razor source if applicable.
    /// </summary>
    public static RazorMappedDiagnostic MapDiagnostic(
        RazorSourceMap sourceMap, Diagnostic diagnostic)
    {
        var location = diagnostic.Location;
        if (location.Kind != LocationKind.SourceFile)
            return new RazorMappedDiagnostic(diagnostic, null);

        var lineSpan = location.GetLineSpan();
        var mappedLocation = MapGeneratedToRazor(
            sourceMap,
            lineSpan.Path,
            lineSpan.StartLinePosition.Line + 1);

        return new RazorMappedDiagnostic(diagnostic, mappedLocation);
    }

    /// <summary>
    /// Discovers all .razor and .cshtml files in a project's directory tree.
    /// </summary>
    public static IEnumerable<string> DiscoverRazorFiles(Project project)
    {
        var projectDir = Path.GetDirectoryName(project.FilePath);
        if (projectDir is null || !Directory.Exists(projectDir))
            yield break;

        foreach (var ext in s_razorExtensions)
        {
            foreach (var file in Directory.EnumerateFiles(projectDir, $"*{ext}", SearchOption.AllDirectories))
            {
                // Skip obj/bin directories
                var relativePath = Path.GetRelativePath(projectDir, file);
                var firstSegment = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                if (firstSegment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                    firstSegment.Equals("bin", StringComparison.OrdinalIgnoreCase))
                    continue;

                yield return file;
            }
        }
    }

    private static bool IsRazorGeneratedDocument(SourceGeneratedDocument doc)
    {
        // Razor source generator typically has "Razor" in the generator type name
        // or the hint name ends with .razor.g.cs / .cshtml.g.cs
        var name = doc.Name ?? doc.FilePath ?? "";
        return name.EndsWith(".razor.g.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".cshtml.g.cs", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Razor", StringComparison.OrdinalIgnoreCase);
    }

    internal static List<RazorLineMapping> ParseLineDirectives(string generatedFilePath, SourceText text)
    {
        var mappings = new List<RazorLineMapping>();
        RazorLineMapping? current = null;

        for (int i = 0; i < text.Lines.Count; i++)
        {
            var line = text.Lines[i];
            var lineText = text.ToString(line.Span).TrimStart();

            if (lineText.StartsWith("#line ", StringComparison.Ordinal))
            {
                int oneBased = i + 1; // 1-indexed line number

                // Close previous mapping (exclusive end = this #line directive)
                if (current is not null)
                {
                    current = current with { GeneratedEndLine = oneBased };
                    mappings.Add(current);
                    current = null;
                }

                if (lineText.StartsWith("#line hidden", StringComparison.Ordinal)
                    || lineText.StartsWith("#line default", StringComparison.Ordinal))
                {
                    continue;
                }

                // Try enhanced C# 10 syntax: #line (startLine, startCol) - (endLine, endCol) [charOffset] "file"
                var enhancedMatch = EnhancedLineDirectiveRegex().Match(lineText);
                if (enhancedMatch.Success)
                {
                    var razorLine = int.Parse(enhancedMatch.Groups[1].Value);
                    var razorFile = enhancedMatch.Groups[2].Value;

                    current = new RazorLineMapping(
                        GeneratedFilePath: generatedFilePath,
                        GeneratedStartLine: oneBased + 1, // directive applies to the NEXT line
                        GeneratedEndLine: null,
                        RazorFilePath: razorFile,
                        RazorLine: razorLine);
                    continue;
                }

                // Standard syntax: #line lineNum "file"
                var match = LineDirectiveRegex().Match(lineText);
                if (match.Success)
                {
                    var razorLine = int.Parse(match.Groups[1].Value);
                    var razorFile = match.Groups[2].Value;

                    current = new RazorLineMapping(
                        GeneratedFilePath: generatedFilePath,
                        GeneratedStartLine: oneBased + 1, // directive applies to the NEXT line
                        GeneratedEndLine: null,
                        RazorFilePath: razorFile,
                        RazorLine: razorLine);
                }
            }
        }

        // Close final mapping (exclusive end = one past last line of file)
        if (current is not null)
        {
            current = current with { GeneratedEndLine = text.Lines.Count + 1 };
            mappings.Add(current);
        }

        return mappings;
    }

    [GeneratedRegex(@"^#line\s+(\d+)\s+""([^""]+)""")]
    private static partial Regex LineDirectiveRegex();

    /// <summary>
    /// Matches the C# 10+ enhanced #line directive:
    /// <c>#line (startLine, startCol) - (endLine, endCol) [charOffset] "filePath"</c>
    /// Group 1 captures startLine; group 2 captures filePath. The character offset is optional.
    /// </summary>
    [GeneratedRegex(@"^#line\s+\((\d+),\s*\d+\)\s*-\s*\(\d+,\s*\d+\)(?:\s+\d+)?\s+""([^""]+)""")]
    private static partial Regex EnhancedLineDirectiveRegex();
}

/// <summary>
/// A complete source map for Razor files in a project.
/// </summary>
internal record RazorSourceMap(
    List<RazorLineMapping> Mappings,
    Dictionary<string, List<SourceGeneratedDocument>> RazorToGeneratedDocuments);

/// <summary>
/// A single line mapping from generated C# to original Razor source.
/// </summary>
internal record RazorLineMapping(
    string GeneratedFilePath,
    int GeneratedStartLine,
    int? GeneratedEndLine,
    string RazorFilePath,
    int RazorLine);

/// <summary>
/// A location mapped back to a Razor source file.
/// </summary>
internal record RazorMappedLocation(string RazorFilePath, int Line);

/// <summary>
/// A diagnostic with optional Razor source mapping.
/// </summary>
internal record RazorMappedDiagnostic(Diagnostic Diagnostic, RazorMappedLocation? MappedLocation);

/// <summary>
/// A location mapped from Razor source to generated C#.
/// </summary>
internal record RazorGeneratedLocation(string GeneratedFilePath, int GeneratedLine);

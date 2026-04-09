using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMCP.Services;

namespace RoslynMCP.Tools.Razor;

/// <summary>
/// Handles symbol rename propagation into Razor files (.razor/.cshtml):
/// uses SymbolFinder + Razor source map for precise code references,
/// and handles component tag and directive renames for type symbols.
/// </summary>
internal class RazorRename : IRenameHandler
{
    /// <summary>
    /// Uses SymbolFinder + Razor source map to find precise reference locations in
    /// generated C# documents, maps them back to Razor source files, and applies
    /// targeted replacements. Also handles component tag renames for type symbols.
    /// </summary>
    public async Task<List<RenameChangedFile>> UpdateReferencesAsync(
        Project project,
        Solution solution,
        ISymbol symbol,
        string oldName,
        string newName,
        CancellationToken cancellationToken)
    {
        var changes = new List<RenameChangedFile>();
        var projectDir = Path.GetDirectoryName(project.FilePath);
        if (projectDir is null || !Directory.Exists(projectDir))
            return changes;

        // Build source map to know which generated docs map to which Razor files
        var sourceMap = await ProjectIndexCacheService.GetRazorSourceMapAsync(project, cancellationToken);
        if (sourceMap.RazorToGeneratedDocuments.Count == 0)
            return changes;

        // Find all references to the symbol in the solution
        var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);

        // Also find references to the symbol definition (for cases where the symbol IS in a Razor file)
        var definitionLocations = symbol.Locations
            .Where(l => l.IsInSource)
            .ToList();

        // Collect Razor lines that need replacement, grouped by file
        var razorReplacements = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);

        foreach (var refGroup in references)
        {
            foreach (var location in refGroup.Locations)
            {
                var doc = solution.GetDocument(location.Document.Id);
                string? docPath = doc?.FilePath ?? doc?.Name;
                if (docPath is null) continue;

                // Check if this document is a generated Razor document
                if (!IsGeneratedRazorDoc(docPath)) continue;

                var lineSpan = location.Location.GetLineSpan();
                int genLine = lineSpan.StartLinePosition.Line + 1;

                var razorLoc = RazorSourceMappingService.MapGeneratedToRazor(sourceMap, docPath, genLine);
                if (razorLoc is null) continue;

                if (!razorReplacements.TryGetValue(razorLoc.RazorFilePath, out var lines))
                {
                    lines = [];
                    razorReplacements[razorLoc.RazorFilePath] = lines;
                }
                lines.Add(razorLoc.Line);
            }
        }

        // Also check definition locations (symbol defined in a Razor file)
        foreach (var loc in definitionLocations)
        {
            string? srcPath = loc.SourceTree?.FilePath;
            if (srcPath is null || !IsGeneratedRazorDoc(srcPath)) continue;

            var lineSpan = loc.GetLineSpan();
            int genLine = lineSpan.StartLinePosition.Line + 1;

            var razorLoc = RazorSourceMappingService.MapGeneratedToRazor(sourceMap, srcPath, genLine);
            if (razorLoc is null) continue;

            if (!razorReplacements.TryGetValue(razorLoc.RazorFilePath, out var lines))
            {
                lines = [];
                razorReplacements[razorLoc.RazorFilePath] = lines;
            }
            lines.Add(razorLoc.Line);
        }

        // Apply replacements in each affected Razor file
        foreach (var (razorFile, affectedLines) in razorReplacements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await File.ReadAllTextAsync(razorFile, cancellationToken);
            var newText = RenameHelper.ReplaceOnLines(text, affectedLines, oldName, newName);

            if (newText != text)
                changes.Add(new RenameChangedFile(razorFile, text, newText));
        }

        // For type renames, also handle component tags and @using directives
        if (symbol is INamedTypeSymbol namedType)
        {
            await UpdateComponentReferencesAsync(
                projectDir, namedType, oldName, newName, changes, cancellationToken);
        }

        return changes;
    }

    /// <summary>
    /// Handles Razor-specific references that don't appear in generated C#:
    /// component tags, @using directives, @inject, @inherits, @implements.
    /// </summary>
    private async Task UpdateComponentReferencesAsync(
        string projectDir,
        INamedTypeSymbol namedType,
        string oldName,
        string newName,
        List<RenameChangedFile> changes,
        CancellationToken cancellationToken)
    {
        // Compute fully-qualified name for directive replacements
        string oldFullName = namedType.ToDisplayString();
        int lastDot = oldFullName.LastIndexOf('.');
        string newFullName = lastDot >= 0
            ? oldFullName[..(lastDot + 1)] + newName
            : newName;

        foreach (var pattern in new[] { "*.razor", "*.cshtml" })
        {
            foreach (var file in Directory.EnumerateFiles(projectDir, pattern, SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relativePath = Path.GetRelativePath(projectDir, file);
                var firstSegment = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                if (firstSegment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                    firstSegment.Equals("bin", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Get the current text (may have been modified by SymbolFinder pass)
                var existingChange = changes.FirstOrDefault(c =>
                    string.Equals(c.FilePath, file, StringComparison.OrdinalIgnoreCase));
                string text = existingChange?.NewText ?? await File.ReadAllTextAsync(file, cancellationToken);
                var newText = text;

                // Replace component tags: <OldName ...> → <NewName ...>, </OldName> → </NewName>
                newText = ReplaceComponentTags(newText, oldName, newName);

                // Replace in directives: @using, @inherits, @implements, @inject
                newText = ReplaceInDirectives(newText, oldName, newName);
                if (oldFullName != oldName)
                    newText = ReplaceInDirectives(newText, oldFullName, newFullName);

                if (newText != text)
                {
                    if (existingChange is not null)
                    {
                        changes.Remove(existingChange);
                        changes.Add(new RenameChangedFile(file, existingChange.OldText, newText));
                    }
                    else
                    {
                        changes.Add(new RenameChangedFile(file, text, newText));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Replaces Blazor component tags in Razor markup.
    /// Handles: &lt;OldName&gt;, &lt;OldName /&gt;, &lt;/OldName&gt;, and &lt;OldName.SubType&gt;
    /// </summary>
    internal static string ReplaceComponentTags(string text, string oldName, string newName)
    {
        var pattern = $@"(</?){Regex.Escape(oldName)}(\s|>|/|\.|\r|\n)";
        return Regex.Replace(text, pattern, m => $"{m.Groups[1].Value}{newName}{m.Groups[2].Value}");
    }

    /// <summary>
    /// Replaces whole-word occurrences in Razor directive lines (@using, @inject, @inherits, @implements).
    /// </summary>
    internal static string ReplaceInDirectives(string text, string oldValue, string newValue)
    {
        var pattern = $@"^(@(?:using|inject|inherits|implements|typeparam)\s+)(.+)$";
        return Regex.Replace(text, pattern, m =>
        {
            var content = m.Groups[2].Value;
            var replaced = Regex.Replace(content, $@"\b{Regex.Escape(oldValue)}\b", newValue);
            return m.Groups[1].Value + replaced;
        }, RegexOptions.Multiline);
    }

    internal static bool IsGeneratedRazorDoc(string path)
    {
        return path.EndsWith(".razor.g.cs", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".cshtml.g.cs", StringComparison.OrdinalIgnoreCase);
    }
}

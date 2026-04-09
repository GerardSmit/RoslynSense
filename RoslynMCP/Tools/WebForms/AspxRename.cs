using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using RoslynMCP.Services;

namespace RoslynMCP.Tools.WebForms;

/// <summary>
/// Handles symbol rename propagation into ASPX/ASCX files:
/// inline code expressions/blocks via the ASPX project index,
/// and directive attributes (Inherits, CodeBehind) via text replacement.
/// </summary>
internal class AspxRename : IRenameHandler
{
    /// <summary>
    /// Uses the ASPX project index to find symbol references in ASPX inline code
    /// (expressions and code blocks), then applies targeted replacements on those lines.
    /// Also handles type-specific directive attributes (Inherits, CodeBehind).
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

        // Build ASPX index for symbol reference search
        var aspxIndex = await ProjectIndexCacheService.GetAspxIndexAsync(project, cancellationToken);

        // Find ASPX files containing references to the symbol name
        var aspxRefs = AspxSourceMappingService.FindSymbolReferences(aspxIndex, oldName);

        // Group by file and collect affected lines
        var aspxReplacements = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var aspxRef in aspxRefs)
        {
            if (!aspxReplacements.TryGetValue(aspxRef.FilePath, out var lines))
            {
                lines = [];
                aspxReplacements[aspxRef.FilePath] = lines;
            }
            lines.Add(aspxRef.Line);
        }

        // Apply whole-word replacements on affected lines only
        foreach (var (aspxFile, affectedLines) in aspxReplacements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await File.ReadAllTextAsync(aspxFile, cancellationToken);
            var newText = RenameHelper.ReplaceOnLines(text, affectedLines, oldName, newName);

            if (newText != text)
                changes.Add(new RenameChangedFile(aspxFile, text, newText));
        }

        // For type renames, also handle directive attributes (Inherits, CodeBehind)
        if (symbol is INamedTypeSymbol namedType)
        {
            await UpdateDirectiveReferencesAsync(
                projectDir, namedType, oldName, newName, changes, cancellationToken);
        }

        return changes;
    }

    /// <summary>
    /// Handles type-specific ASPX directive attributes (Inherits, CodeBehind) that aren't
    /// represented as inline code expressions/blocks.
    /// </summary>
    private async Task UpdateDirectiveReferencesAsync(
        string projectDir,
        INamedTypeSymbol namedType,
        string oldName,
        string newName,
        List<RenameChangedFile> changes,
        CancellationToken cancellationToken)
    {
        string oldFullName = namedType.ToDisplayString();
        int lastDot = oldFullName.LastIndexOf('.');
        string newFullName = lastDot >= 0
            ? oldFullName[..(lastDot + 1)] + newName
            : newName;

        string[] aspxExtensions = ["*.aspx", "*.ascx", "*.master", "*.asmx", "*.ashx", "*.asax"];

        foreach (var pattern in aspxExtensions)
        {
            foreach (var file in Directory.EnumerateFiles(projectDir, pattern, SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relativePath = Path.GetRelativePath(projectDir, file);
                var firstSegment = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                if (firstSegment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                    firstSegment.Equals("bin", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Get current text (may have been modified by FindSymbolReferences pass)
                var existingChange = changes.FirstOrDefault(c =>
                    string.Equals(c.FilePath, file, StringComparison.OrdinalIgnoreCase));
                var text = existingChange?.NewText ?? await File.ReadAllTextAsync(file, cancellationToken);
                var newText = text;

                // Replace fully-qualified type name in Inherits="..." attributes
                newText = ReplaceDirectiveAttribute(newText, "Inherits", oldFullName, newFullName);
                if (!oldFullName.Equals(oldName))
                    newText = ReplaceDirectiveAttribute(newText, "Inherits", oldName, newName);

                // Replace in CodeBehind/CodeFile attributes (file name part)
                newText = ReplaceCodeBehindFileName(newText, oldName, newName);

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
    /// Replaces an attribute value in ASPX directives.
    /// E.g., Inherits="OldName" → Inherits="NewName"
    /// </summary>
    internal static string ReplaceDirectiveAttribute(
        string text, string attributeName, string oldValue, string newValue)
    {
        var pattern = $@"({Regex.Escape(attributeName)}\s*=\s*"")({Regex.Escape(oldValue)})("")";
        return Regex.Replace(text, pattern, $"${{1}}{newValue}${{3}}");
    }

    /// <summary>
    /// Replaces type names in CodeBehind/CodeFile attribute values.
    /// </summary>
    internal static string ReplaceCodeBehindFileName(string text, string oldName, string newName)
    {
        var pattern = $@"(Code(?:Behind|File)\s*=\s*""[^""]*){Regex.Escape(oldName)}([^""]*"")";
        return Regex.Replace(text, pattern, $"${{1}}{newName}${{2}}");
    }

    /// <summary>
    /// Replaces whole-word occurrences inside &lt;% ... %&gt; code blocks.
    /// </summary>
    internal static string ReplaceInCodeBlocks(string text, string oldName, string newName)
    {
        return Regex.Replace(text, @"(<%[=#:]?\s*)(.*?)(\s*%>)", m =>
        {
            var code = m.Groups[2].Value;
            var replaced = Regex.Replace(code, $@"\b{Regex.Escape(oldName)}\b", newName);
            return m.Groups[1].Value + replaced + m.Groups[3].Value;
        }, RegexOptions.Singleline);
    }
}

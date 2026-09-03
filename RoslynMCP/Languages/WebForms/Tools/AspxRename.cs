using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services;
using RoslynMCP.Tools;
using RoslynMCP.Languages.WebForms.Core;

namespace RoslynMCP.Languages.WebForms.Tools;

/// <summary>
/// Handles symbol rename propagation into ASPX/ASCX files: every tag, attribute and inline-code
/// mention the reference search binds to the symbol, plus the directive attributes (Inherits,
/// CodeBehind) that name a type without referring to it.
/// </summary>
internal class AspxRename : IRenameHandler
{
    /// <summary>
    /// Rewrites the markup spans that refer to <paramref name="symbol"/>, then the directive
    /// attributes naming it.
    /// </summary>
    /// <remarks>
    /// The spans come from the same reference search the editor's rename uses, so a rename
    /// applied here and one applied from VS Code touch exactly the same characters. It also
    /// replaced a name search: matching <paramref name="oldName"/> as text rewrote the word
    /// wherever it appeared, including inside comments and string literals in a
    /// <c>&lt;% %&gt;</c> block, which silently corrupted the page.
    /// </remarks>
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

        var references = await AspxReferenceService.FindAsync(symbol, project, cancellationToken);

        foreach (var group in references.GroupBy(r => r.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var text = group.First().Text;
            var updated = text.WithChanges(Edits(group, newName));
            if (updated.ContentEquals(text))
                continue;

            changes.Add(new RenameChangedFile(group.Key, text.ToString(), updated.ToString()));
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
    /// One file's replacements, ascending and non-overlapping — what
    /// <see cref="SourceText.WithChanges(IEnumerable{TextChange})"/> requires. Two searches feed
    /// the same list (the markup walk and the projected C#), so a place found by both would
    /// otherwise make the whole rename throw rather than lose one edit.
    /// </summary>
    private static IEnumerable<TextChange> Edits(IEnumerable<AspxReference> references, string newName)
    {
        int applied = 0;

        foreach (var reference in references.OrderBy(r => r.Span.Start).ThenBy(r => r.Span.End))
        {
            if (reference.Span.Start < applied)
                continue;

            applied = reference.Span.End;
            yield return new TextChange(reference.Span, AspxReferenceService.RenamedText(reference, newName));
        }
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

                // Get current text (may already carry the reference-span edits above)
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

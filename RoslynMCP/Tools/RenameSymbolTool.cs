using System.Collections.Immutable;
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Rename;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

/// <summary>
/// Renames a symbol and all its references across the project using Roslyn's semantic rename engine.
/// Also updates ASPX/ASCX file references (Inherits, CodeBehind directives and inline code).
/// </summary>
[McpServerToolType]
public static class RenameSymbolTool
{
    /// <summary>
    /// Renames a symbol identified by a markup snippet and applies changes to disk.
    /// </summary>
    [McpServerTool, Description(
        "Rename a symbol and all its references across the project. Provide a code snippet " +
        "with [| |] delimiters around the symbol to rename, e.g. 'var x = [|OldName|]();'. " +
        "All references in the project are updated, including ASPX/ASCX files. " +
        "When renaming a type whose name matches its file name, the file is also renamed. " +
        "Returns a summary of changed files.")]
    public static async Task<string> RenameSymbol(
        [Description("Path to the file containing the symbol.")] string filePath,
        [Description(
            "Code snippet with [| |] markers around the symbol to rename, " +
            "e.g. 'void [|OldName|](int x)'.")]
        string markupSnippet,
        [Description("The new name for the symbol.")] string newName,
        [Description("If true, show a preview of changes without applying them. Default: false.")]
        bool dryRun = false,
        [Description("If true, also rename overloaded methods with the same name. Default: false.")]
        bool renameOverloads = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(newName))
                return "Error: newName cannot be empty.";

            if (!IsValidIdentifier(newName))
                return $"Error: '{newName}' is not a valid C# identifier.";

            var errors = new StringBuilder();
            var ctx = await ToolHelper.ResolveSymbolAsync(filePath, markupSnippet, errors, cancellationToken);
            if (ctx is null)
                return errors.ToString();

            if (!ctx.IsResolved)
                return ToolHelper.FormatResolutionError(ctx.Resolution);

            var symbol = ctx.Symbol!;
            string oldName = symbol.Name;

            if (oldName == newName)
                return $"Symbol '{oldName}' already has the requested name.";

            if (symbol.Kind is SymbolKind.Namespace or SymbolKind.Assembly or SymbolKind.NetModule)
                return $"Error: Cannot rename {symbol.Kind} symbols.";

            // Perform the Roslyn rename (C# files)
            var solution = ctx.Workspace.CurrentSolution;
            var renameOptions = new SymbolRenameOptions(
                RenameOverloads: renameOverloads,
                RenameInStrings: false,
                RenameInComments: false,
                RenameFile: false);

            var newSolution = await Renamer.RenameSymbolAsync(
                solution, symbol, renameOptions, newName, cancellationToken);

            // Collect changed C# documents
            var changedDocs = new List<ChangedFile>();

            foreach (var projectId in newSolution.ProjectIds)
            {
                var oldProject = solution.GetProject(projectId);
                var newProject = newSolution.GetProject(projectId);
                if (oldProject is null || newProject is null) continue;

                foreach (var docId in newProject.DocumentIds)
                {
                    var oldDoc = oldProject.GetDocument(docId);
                    var newDoc = newProject.GetDocument(docId);
                    if (oldDoc is null || newDoc is null) continue;

                    var oldText = await oldDoc.GetTextAsync(cancellationToken);
                    var newText = await newDoc.GetTextAsync(cancellationToken);

                    if (oldText.ToString() != newText.ToString())
                    {
                        changedDocs.Add(new ChangedFile(
                            oldDoc.FilePath ?? oldDoc.Name,
                            oldText.ToString(),
                            newText.ToString()));
                    }
                }
            }

            // Update ASPX/ASCX files if renaming a type
            var aspxChanges = new List<ChangedFile>();
            if (symbol is INamedTypeSymbol namedType)
            {
                aspxChanges = await UpdateAspxReferencesAsync(
                    ctx.Project.FilePath!, namedType, oldName, newName, cancellationToken);
            }

            // Determine file renames (type name matches file name)
            var fileRenames = new List<(string OldPath, string NewPath)>();
            if (symbol is INamedTypeSymbol)
            {
                foreach (var loc in symbol.Locations)
                {
                    if (loc.IsInSource && loc.SourceTree?.FilePath is string srcPath)
                    {
                        string fileNameNoExt = Path.GetFileNameWithoutExtension(srcPath);
                        if (fileNameNoExt.Equals(oldName, StringComparison.OrdinalIgnoreCase))
                        {
                            string ext = Path.GetExtension(srcPath);
                            string dir = Path.GetDirectoryName(srcPath)!;
                            string newPath = Path.Combine(dir, newName + ext);
                            if (!File.Exists(newPath))
                                fileRenames.Add((srcPath, newPath));
                        }
                    }
                }
            }

            int totalChanges = changedDocs.Count + aspxChanges.Count;
            if (totalChanges == 0 && fileRenames.Count == 0)
                return $"No changes were produced when renaming '{oldName}' to '{newName}'.";

            var sb = new StringBuilder();
            sb.AppendLine($"# Rename: {oldName} → {newName}");
            sb.AppendLine();
            sb.AppendLine($"- **Symbol**: {symbol.ToDisplayString()}");
            sb.AppendLine($"- **Kind**: {symbol.Kind}");
            sb.AppendLine($"- **C# files changed**: {changedDocs.Count}");
            if (aspxChanges.Count > 0)
                sb.AppendLine($"- **ASPX/ASCX files changed**: {aspxChanges.Count}");
            if (fileRenames.Count > 0)
                sb.AppendLine($"- **Files renamed**: {fileRenames.Count}");
            sb.AppendLine($"- **Mode**: {(dryRun ? "Preview (no changes written)" : "Applied")}");
            sb.AppendLine();

            if (!dryRun)
            {
                // Write C# changes
                foreach (var change in changedDocs)
                {
                    if (!string.IsNullOrEmpty(change.FilePath) && File.Exists(change.FilePath))
                        await File.WriteAllTextAsync(change.FilePath, change.NewText, cancellationToken);
                }

                // Write ASPX changes
                foreach (var change in aspxChanges)
                {
                    if (!string.IsNullOrEmpty(change.FilePath) && File.Exists(change.FilePath))
                        await File.WriteAllTextAsync(change.FilePath, change.NewText, cancellationToken);
                }

                // Rename files
                foreach (var (oldPath, newPath) in fileRenames)
                {
                    if (File.Exists(oldPath) && !File.Exists(newPath))
                        File.Move(oldPath, newPath);
                }

                ProjectIndexCacheService.InvalidateProject(ctx.Project.FilePath!);
            }

            // Summary table
            sb.AppendLine("## Changed Files");
            sb.AppendLine();
            sb.AppendLine("| File | Type | Changes |");
            sb.AppendLine("|------|------|---------|");

            string? projectDir = ctx.ProjectDir;

            foreach (var change in changedDocs)
            {
                string displayPath = projectDir is not null
                    ? Path.GetRelativePath(projectDir, change.FilePath)
                    : change.FilePath;
                int changeCount = CountOccurrences(change.NewText, newName) - CountOccurrences(change.OldText, newName);
                sb.AppendLine($"| {MarkdownHelper.EscapeTableCell(displayPath)} | C# | {changeCount} occurrence(s) |");
            }

            foreach (var change in aspxChanges)
            {
                string displayPath = projectDir is not null
                    ? Path.GetRelativePath(projectDir, change.FilePath)
                    : change.FilePath;
                int changeCount = CountOccurrences(change.NewText, newName) - CountOccurrences(change.OldText, newName);
                sb.AppendLine($"| {MarkdownHelper.EscapeTableCell(displayPath)} | ASPX | {changeCount} occurrence(s) |");
            }

            if (fileRenames.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Renamed Files");
                sb.AppendLine();
                foreach (var (oldPath, newPath) in fileRenames)
                {
                    string oldDisplay = projectDir is not null ? Path.GetRelativePath(projectDir, oldPath) : oldPath;
                    string newDisplay = projectDir is not null ? Path.GetRelativePath(projectDir, newPath) : newPath;
                    sb.AppendLine($"- {oldDisplay} → {newDisplay}");
                }
            }

            return sb.ToString();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RenameSymbol] Unhandled error: {ex}");
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Scans ASPX/ASCX/Master/ASMX/ASHX files in the project directory for references
    /// to the renamed type in directives (Inherits, CodeBehind) and inline code blocks.
    /// </summary>
    private static async Task<List<ChangedFile>> UpdateAspxReferencesAsync(
        string projectPath,
        INamedTypeSymbol namedType,
        string oldName,
        string newName,
        CancellationToken cancellationToken)
    {
        var changes = new List<ChangedFile>();
        var projectDir = Path.GetDirectoryName(projectPath);
        if (projectDir is null || !Directory.Exists(projectDir))
            return changes;

        string oldFullName = namedType.ToDisplayString();
        // Only replace the type name (last segment) to avoid mangling the namespace
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

                // Skip obj/bin
                var relativePath = Path.GetRelativePath(projectDir, file);
                var firstSegment = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                if (firstSegment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                    firstSegment.Equals("bin", StringComparison.OrdinalIgnoreCase))
                    continue;

                var text = await File.ReadAllTextAsync(file, cancellationToken);
                var newText = text;

                // Replace fully-qualified type name in Inherits="..." attributes
                newText = ReplaceDirectiveAttribute(newText, "Inherits", oldFullName, newFullName);

                // Replace short name in Inherits if used without namespace
                if (!oldFullName.Equals(oldName))
                    newText = ReplaceDirectiveAttribute(newText, "Inherits", oldName, newName);

                // Replace in CodeBehind/CodeFile attributes (file name part)
                newText = ReplaceCodeBehindFileName(newText, oldName, newName);

                // Replace in inline code blocks (<% %>, <%= %>, <%# %>)
                newText = ReplaceInCodeBlocks(newText, oldName, newName);

                if (newText != text)
                {
                    changes.Add(new ChangedFile(file, text, newText));
                }
            }
        }

        return changes;
    }

    /// <summary>
    /// Replaces an attribute value in ASPX directives.
    /// E.g., Inherits="OldName" → Inherits="NewName"
    /// </summary>
    internal static string ReplaceDirectiveAttribute(
        string text, string attributeName, string oldValue, string newValue)
    {
        // Match attributeName="...oldValue..."
        var pattern = $@"({Regex.Escape(attributeName)}\s*=\s*"")([^""]*{Regex.Escape(oldValue)}[^""]*)("")";
        return Regex.Replace(text, pattern, m =>
        {
            var attrValue = m.Groups[2].Value;
            var replaced = attrValue.Replace(oldValue, newValue);
            return m.Groups[1].Value + replaced + m.Groups[3].Value;
        }, RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Replaces the file name portion of CodeBehind/CodeFile attributes.
    /// E.g., CodeBehind="OldName.aspx.cs" → CodeBehind="NewName.aspx.cs"
    /// </summary>
    internal static string ReplaceCodeBehindFileName(string text, string oldName, string newName)
    {
        var pattern = $@"((?:CodeBehind|CodeFile)\s*=\s*"")({Regex.Escape(oldName)})(\.[\w.]+)("")";
        return Regex.Replace(text, pattern, $"${{1}}{newName}$3$4", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Replaces symbol references within ASPX code blocks (&lt;% %&gt;, &lt;%= %&gt;, &lt;%# %&gt;).
    /// Uses word-boundary matching to avoid partial replacements.
    /// </summary>
    internal static string ReplaceInCodeBlocks(string text, string oldName, string newName)
    {
        // Match <% ... %> blocks and replace whole-word occurrences of oldName
        return Regex.Replace(text, @"(<%[=#:]?\s*)(.*?)(\s*%>)", m =>
        {
            var code = m.Groups[2].Value;
            var replaced = Regex.Replace(code, $@"\b{Regex.Escape(oldName)}\b", newName);
            return m.Groups[1].Value + replaced + m.Groups[3].Value;
        }, RegexOptions.Singleline);
    }

    private static bool IsValidIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        var startIndex = name[0] == '@' ? 1 : 0;
        if (startIndex >= name.Length)
            return false;

        if (!char.IsLetter(name[startIndex]) && name[startIndex] != '_')
            return false;

        for (int i = startIndex + 1; i < name.Length; i++)
        {
            if (!char.IsLetterOrDigit(name[i]) && name[i] != '_')
                return false;
        }

        return true;
    }

    private static int CountOccurrences(string text, string search)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(search, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += search.Length;
        }
        return count;
    }

    private sealed record ChangedFile(string FilePath, string OldText, string NewText);
}

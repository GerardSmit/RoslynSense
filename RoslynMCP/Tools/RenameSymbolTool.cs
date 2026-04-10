using System.ComponentModel;
using System.Text;
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
        "All references in the project are updated, including ASPX/ASCX and Razor files. " +
        "When renaming a type whose name matches its file name, the file is also renamed. " +
        "Returns a summary of changed files.")]
    public static async Task<string> RenameSymbol(
        [Description("Path to the file containing the symbol.")] string filePath,
        [Description(
            "Code snippet with [| |] markers around the symbol to rename, " +
            "e.g. 'void [|OldName|](int x)'.")]
        string markupSnippet,
        [Description("The new name for the symbol.")] string newName,
        IOutputFormatter fmt,
        [Description("If true, show a preview of changes without applying them. Default: false.")]
        bool dryRun = false,
        [Description("If true, also rename overloaded methods with the same name. Default: false.")]
        bool renameOverloads = false,
        [Description("Approximate line number near the target snippet. Used to pick the closest match when the snippet appears multiple times.")]
        int? hintLine = null,
        IEnumerable<IRenameHandler>? handlers = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(newName))
                return "Error: newName cannot be empty.";

            if (!IsValidIdentifier(newName))
                return $"Error: '{newName}' is not a valid C# identifier.";

            var errors = new StringBuilder();
            var ctx = await ToolHelper.ResolveSymbolAsync(filePath, markupSnippet, errors, cancellationToken, hintLine);
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
            var changedDocs = new List<RenameChangedFile>();

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

                    if (!oldText.ContentEquals(newText))
                    {
                        changedDocs.Add(new RenameChangedFile(
                            oldDoc.FilePath ?? oldDoc.Name,
                            oldText.ToString(),
                            newText.ToString()));
                    }
                }
            }

            // Update non-C# files via registered handlers (ASPX, Razor, etc.)
            var nonCSharpChanges = new List<RenameChangedFile>();
            if (handlers is not null)
            {
                foreach (var handler in handlers)
                {
                    var changes = await handler.UpdateReferencesAsync(
                        ctx.Project, solution, symbol, oldName, newName, cancellationToken);
                    nonCSharpChanges.AddRange(changes);
                }
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

            int totalChanges = changedDocs.Count + nonCSharpChanges.Count;
            if (totalChanges == 0 && fileRenames.Count == 0)
                return $"No changes were produced when renaming '{oldName}' to '{newName}'.";
            var sb = new StringBuilder();
            fmt.AppendHeader(sb, $"Rename: {oldName} → {newName}");
            fmt.AppendField(sb, "Symbol", symbol.ToDisplayString());
            fmt.AppendField(sb, "Kind", symbol.Kind);
            fmt.AppendField(sb, "C# files changed", changedDocs.Count);
            if (nonCSharpChanges.Count > 0)
                fmt.AppendField(sb, "Non-C# files changed", nonCSharpChanges.Count);
            if (fileRenames.Count > 0)
                fmt.AppendField(sb, "Files renamed", fileRenames.Count);
            fmt.AppendField(sb, "Mode", dryRun ? "Preview (no changes written)" : "Applied");
            fmt.AppendSeparator(sb);

            if (!dryRun)
            {
                // Write C# changes
                foreach (var change in changedDocs)
                {
                    if (!string.IsNullOrEmpty(change.FilePath) && File.Exists(change.FilePath))
                        await File.WriteAllTextAsync(change.FilePath, change.NewText, cancellationToken);
                }

                // Write non-C# changes
                foreach (var change in nonCSharpChanges)
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
            string? projectDir = ctx.ProjectDir;

            var columns = new[] { "File", "Type", "Changes" };
            var rows = new List<string[]>();

            foreach (var change in changedDocs)
            {
                string displayPath = projectDir is not null
                    ? Path.GetRelativePath(projectDir, change.FilePath)
                    : change.FilePath;
                int changeCount = RenameHelper.CountOccurrences(change.NewText, newName) - RenameHelper.CountOccurrences(change.OldText, newName);
                rows.Add([fmt.Escape(displayPath), "C#", $"{changeCount} occurrence(s)"]);
            }

            foreach (var change in nonCSharpChanges)
            {
                string displayPath = projectDir is not null
                    ? Path.GetRelativePath(projectDir, change.FilePath)
                    : change.FilePath;
                int changeCount = RenameHelper.CountOccurrences(change.NewText, newName) - RenameHelper.CountOccurrences(change.OldText, newName);
                string ext = Path.GetExtension(change.FilePath).TrimStart('.').ToUpperInvariant();
                rows.Add([fmt.Escape(displayPath), ext, $"{changeCount} occurrence(s)"]);
            }

            fmt.AppendTable(sb, "Changed Files", columns, rows);

            if (fileRenames.Count > 0)
            {
                fmt.AppendHeader(sb, "Renamed Files", 2);
                var renameColumns = new[] { "Old Path", "New Path" };
                var renameRows = new List<string[]>();
                foreach (var (oldPath, newPath) in fileRenames)
                {
                    string oldDisplay = projectDir is not null ? Path.GetRelativePath(projectDir, oldPath) : oldPath;
                    string newDisplay = projectDir is not null ? Path.GetRelativePath(projectDir, newPath) : newPath;
                    renameRows.Add([fmt.Escape(oldDisplay), fmt.Escape(newDisplay)]);
                }
                fmt.AppendTable(sb, "Renamed Files", renameColumns, renameRows);
            }

            fmt.AppendHints(sb, "Use GetRoslynDiagnostics to verify no issues after rename");

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
}

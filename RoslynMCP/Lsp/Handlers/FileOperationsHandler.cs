using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Rename;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Services.ProjectModel;

namespace RoslynMCP.Lsp.Handlers;

public sealed record RenameFilesParams(
    [property: JsonPropertyName("files")] FileRename[] Files);

public sealed record FileRename(
    [property: JsonPropertyName("oldUri")] string OldUri,
    [property: JsonPropertyName("newUri")] string NewUri);

/// <summary>
/// workspace/willRenameFiles: renaming Foo.cs to Bar.cs should rename the type inside it and
/// every reference to it, which is what Visual Studio and Rider do. Returning the edit from
/// <em>will</em>Rename means the editor applies it as part of the same undo step as the rename.
/// </summary>
internal static class FileOperationsHandler
{
    /// <summary>A rename that would rewrite more than this many files is almost certainly not
    /// what the user meant by dragging one file; it is skipped rather than applied silently.</summary>
    private const int MaxAffectedFiles = 200;

    public static async Task<WorkspaceEdit?> WillRenameAsync(RenameFilesParams p, CancellationToken ct)
    {
        var changes = new Dictionary<string, List<TextEdit>>(StringComparer.OrdinalIgnoreCase);

        foreach (var rename in p.Files)
        {
            ct.ThrowIfCancellationRequested();

            string oldPath = LspConverters.UriToPath(rename.OldUri);
            string newPath = LspConverters.UriToPath(rename.NewUri);

            if (!oldPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                !newPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                continue;

            string oldName = Path.GetFileNameWithoutExtension(oldPath);
            string newName = Path.GetFileNameWithoutExtension(newPath);
            if (oldName.Equals(newName, StringComparison.Ordinal) || !IsIdentifier(newName))
                continue;

            var edits = await RenameMatchingTypeAsync(oldPath, oldName, newName, ct);
            foreach (var (uri, fileEdits) in edits)
            {
                if (changes.TryGetValue(uri, out var existing))
                    existing.AddRange(fileEdits);
                else
                    changes[uri] = [.. fileEdits];
            }
        }

        return changes.Count == 0
            ? null
            : new WorkspaceEdit(changes.ToDictionary(c => c.Key, c => c.Value.ToArray()));
    }

    /// <summary>
    /// Renames the type whose name matches the file's, if there is one. A file holding several
    /// types, or a type whose name never matched the file, is left alone — guessing there
    /// would rewrite code the user did not ask about.
    /// </summary>
    private static async Task<Dictionary<string, TextEdit[]>> RenameMatchingTypeAsync(
        string filePath, string oldName, string newName, CancellationToken ct)
    {
        var document = await LspDocumentResolver.ResolveAsync(filePath, ct);
        if (document is null)
            return [];

        var root = await document.GetSyntaxRootAsync(ct);
        var model = await document.GetSemanticModelAsync(ct);
        if (root is null || model is null)
            return [];

        var declaration = root.DescendantNodes()
            .OfType<BaseTypeDeclarationSyntax>()
            .FirstOrDefault(t => t.Identifier.ValueText.Equals(oldName, StringComparison.Ordinal));
        if (declaration is null || model.GetDeclaredSymbol(declaration, ct) is not { } symbol)
            return [];

        try
        {
            var solution = document.Project.Solution;
            var renamed = await Renamer.RenameSymbolAsync(
                solution, symbol, new SymbolRenameOptions(), newName, ct);

            var changedDocuments = renamed.GetChanges(solution)
                .GetProjectChanges()
                .SelectMany(project => project.GetChangedDocuments())
                .Distinct()
                .ToList();

            if (changedDocuments.Count > MaxAffectedFiles)
            {
                ServiceLog.Warn(
                    $"Renaming '{oldName}' would touch {changedDocuments.Count} files; skipped the " +
                    "symbol rename and renamed only the file.",
                    key: $"rename-too-large:{filePath}");
                return [];
            }

            var edits = new Dictionary<string, TextEdit[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in changedDocuments)
            {
                var before = solution.GetDocument(id);
                var after = renamed.GetDocument(id);
                if (before?.FilePath is not { Length: > 0 } path || after is null)
                    continue;

                var oldText = await before.GetTextAsync(ct);
                var textChanges = await after.GetTextChangesAsync(before, ct);

                var fileEdits = textChanges
                    .Select(change => new TextEdit(
                        LspConverters.ToRange(oldText.Lines, change.Span), change.NewText ?? ""))
                    .ToArray();

                if (fileEdits.Length > 0)
                    edits[LspConverters.PathToUri(path)] = fileEdits;
            }

            return edits;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ServiceLog.Warn($"Could not rename '{oldName}' to '{newName}': {ex.Message}",
                key: $"rename-failed:{filePath}");
            return [];
        }
    }

    /// <summary>
    /// workspace/didCreateFiles: a <c>.cs</c> file made through the editor's own explorer
    /// arrives empty, so it gets the namespace and type the Solution Explorer's "New file"
    /// would have given it, and its project gets a compile item if it needs one.
    /// </summary>
    /// <remarks>
    /// This is <em>did</em>Create rather than <em>will</em>Create on purpose: an edit against a
    /// URI that does not exist yet is not something a client is obliged to apply, and VS Code
    /// does not. Sending it afterwards through <c>workspace/applyEdit</c> lands in the buffer
    /// the user is already looking at, and in their undo stack.
    /// </remarks>
    public static async Task DidCreateAsync(CreateFilesParams p, CancellationToken ct)
    {
        foreach (var file in p.Files)
        {
            ct.ThrowIfCancellationRequested();

            string path = LspConverters.UriToPath(file.Uri);
            string? scaffold;
            try
            {
                scaffold = await ProjectMutationService.ScaffoldNewFileAsync(path, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ServiceLog.Warn($"Could not set up '{Path.GetFileName(path)}': {ex.Message}",
                    key: $"scaffold-failed:{path}");
                continue;
            }

            if (scaffold is not { Length: > 0 })
                continue;

            // The editor owns the buffer; writing the file underneath it would be overwritten
            // by the next save. Fall back to disk only when no session took the edit.
            if (!await LspSessionRegistry.TryApplyFullTextEditAsync(
                    path, scaffold, $"Set up {Path.GetFileName(path)}", ct))
            {
                try { await File.WriteAllTextAsync(path, scaffold, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    ServiceLog.Warn($"Could not write '{Path.GetFileName(path)}': {ex.Message}",
                        key: $"scaffold-write-failed:{path}");
                }
            }
        }

        await LspSessionRegistry.RequestRefreshAsync(RefreshKind.All, ct);
    }

    /// <summary>
    /// workspace/didDeleteFiles: drops the file from its project's item list and from the
    /// loaded workspace, so a legacy project does not keep a <c>&lt;Compile&gt;</c> pointing at
    /// nothing and the next navigation does not resolve into a file that is gone.
    /// </summary>
    public static async Task DidDeleteAsync(DeleteFilesParams p, CancellationToken ct)
    {
        bool touched = false;
        foreach (var file in p.Files)
        {
            ct.ThrowIfCancellationRequested();

            string path = LspConverters.UriToPath(file.Uri);
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                await ProjectMutationService.ForgetDeletedFileAsync(path, ct);
                touched = true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ServiceLog.Warn($"Could not clean up after '{Path.GetFileName(path)}': {ex.Message}",
                    key: $"delete-cleanup-failed:{path}");
            }
        }

        if (touched)
            await LspSessionRegistry.RequestRefreshAsync(RefreshKind.All, ct);
    }

    private static bool IsIdentifier(string name) =>
        name.Length > 0 &&
        (char.IsLetter(name[0]) || name[0] == '_') &&
        name.All(c => char.IsLetterOrDigit(c) || c == '_');
}

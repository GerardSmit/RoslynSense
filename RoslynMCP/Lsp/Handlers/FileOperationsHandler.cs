using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Rename;
using RoslynMCP.Languages;
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
/// <remarks>
/// C# is the host language, so the <c>.cs</c> path is inline here; every other file type belongs
/// to whichever pack claims its extension, and each is handed only the renames it owns. A pack
/// puts its text edits in <see cref="WorkspaceEdit.Changes"/> and its file moves in
/// <see cref="WorkspaceEdit.DocumentChanges"/> — combining the two into the one ordered array the
/// wire needs is this handler's job, because only it sees every pack's answer at once.
/// </remarks>
internal static class FileOperationsHandler
{
    /// <summary>A rename that would rewrite more than this many files is almost certainly not
    /// what the user meant by dragging one file; it is skipped rather than applied silently.</summary>
    private const int MaxAffectedFiles = 200;

    public static async Task<WorkspaceEdit?> WillRenameAsync(
        RenameFilesParams p, CancellationToken ct, LanguageSession? languages = null)
    {
        var changes = new Dictionary<string, List<TextEdit>>(StringComparer.OrdinalIgnoreCase);
        var operations = new List<object>();

        foreach (var rename in p.Files)
        {
            ct.ThrowIfCancellationRequested();

            string oldPath = LspConverters.UriToPath(rename.OldUri);
            string newPath = LspConverters.UriToPath(rename.NewUri);

            if (!oldPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                !newPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                continue;

            // The project item first, and for a move as much as for a rename — the two cases
            // below both skip a file that kept its name, which is exactly what a drag in the
            // explorer is. A project that lists its sources by hand is left naming a path that is
            // gone: the file stops compiling, no document is ever created for its new path, and
            // everything the server offers over it — lenses, navigation, diagnostics — is quietly
            // missing with nothing on screen to say why. An SDK project globs and has no item to
            // move, which this treats as success.
            await ProjectMutationService.RenameFileItemAsync(oldPath, newPath, ct);

            string oldName = Path.GetFileNameWithoutExtension(oldPath);
            string newName = Path.GetFileNameWithoutExtension(newPath);
            if (oldName.Equals(newName, StringComparison.Ordinal) || !IsIdentifier(newName))
                continue;

            Merge(changes, await RenameMatchingTypeAsync(oldPath, oldName, newName, ct));
        }

        foreach (var (provider, files) in ByPack(p.Files, f => f.OldUri, languages))
        {
            ct.ThrowIfCancellationRequested();

            if (await provider.WillRenameAsync(new RenameFilesParams(files), ct) is not { } edit)
                continue;

            Merge(changes, edit.Changes);
            operations.AddRange(edit.DocumentChanges ?? []);
        }

        if (changes.Count == 0 && operations.Count == 0)
            return null;

        var byUri = changes.ToDictionary(
            c => c.Key, c => c.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        if (operations.Count == 0)
            return new WorkspaceEdit(byUri);

        // The client applies documentChanges in order, and every edit is keyed on the path its
        // file still has, so the text has to be rewritten before anything moves.
        return new WorkspaceEdit(byUri,
        [
            .. byUri.Select(entry => new TextDocumentEdit(
                new OptionalVersionedTextDocumentIdentifier(entry.Key), entry.Value)),
            .. operations,
        ]);
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

        return await RenameSymbolEditsAsync(symbol, document.Project.Solution, newName, ct);
    }

    /// <summary>
    /// The edits Roslyn's own rename produces for a symbol, as LSP edits keyed by document URI.
    /// </summary>
    /// <remarks>
    /// Shared with the language packs: renaming <c>Default.aspx</c> renames the code-behind class
    /// it names, which is the same gesture reached from the other side, and the ceiling below has
    /// to apply to both. The rename is not applied to the workspace — a <em>will</em>Rename
    /// answer is a proposal the client applies, or does not.
    /// </remarks>
    internal static async Task<Dictionary<string, TextEdit[]>> RenameSymbolEditsAsync(
        ISymbol symbol, Solution solution, string newName, CancellationToken ct)
    {
        try
        {
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
                    $"Renaming '{symbol.Name}' would touch {changedDocuments.Count} files; skipped " +
                    "the symbol rename and renamed only the file.",
                    key: $"rename-too-large:{symbol.Name}");
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
            ServiceLog.Warn($"Could not rename '{symbol.Name}' to '{newName}': {ex.Message}",
                key: $"rename-failed:{symbol.Name}");
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
    public static async Task DidCreateAsync(
        CreateFilesParams p, CancellationToken ct, LanguageSession? languages = null)
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

        foreach (var (provider, files) in ByPack(p.Files, f => f.Uri, languages))
            await provider.DidCreateAsync(new CreateFilesParams(files), ct);

        await LspSessionRegistry.RequestRefreshAsync(RefreshKind.All, ct);
    }

    /// <summary>
    /// workspace/didDeleteFiles: drops the file from its project's item list and from the
    /// loaded workspace, so a legacy project does not keep a <c>&lt;Compile&gt;</c> pointing at
    /// nothing and the next navigation does not resolve into a file that is gone.
    /// </summary>
    /// <remarks>
    /// Deliberately narrower than the rename: a deleted <c>.aspx</c> leaves its code-behind and
    /// designer where they are. The editor deleted exactly what the user selected, and deleting
    /// files it did not ask about is not something an undo of the delete would put back.
    /// </remarks>
    public static async Task DidDeleteAsync(
        DeleteFilesParams p, CancellationToken ct, LanguageSession? languages = null)
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

        foreach (var (provider, files) in ByPack(p.Files, f => f.Uri, languages))
        {
            await provider.DidDeleteAsync(new DeleteFilesParams(files), ct);
            touched = true;
        }

        if (touched)
            await LspSessionRegistry.RequestRefreshAsync(RefreshKind.All, ct);
    }

    /// <summary>
    /// The files grouped by the pack that owns them, skipping any the packs do not claim — those
    /// are C#'s, or nobody's. Resolution is by extension through the session rather than by
    /// re-matching the globs the pack declared, so the routing cannot drift from the filters the
    /// server registered for that connection. A call with no session came from an MCP tool, whose
    /// gate is the registration one — see <see cref="LanguageScope"/>.
    /// </summary>
    private static List<(ILanguageFileOperationProvider Provider, T[] Files)> ByPack<T>(
        IEnumerable<T> files, Func<T, string> uriOf, LanguageSession? languages)
    {
        var session = LanguageScope.Of(languages);
        var groups = new Dictionary<ILanguageFileOperationProvider, List<T>>();

        foreach (var file in files)
        {
            if (session.Resolve<ILanguageFileOperationProvider>(uriOf(file)) is not { } provider)
                continue;

            if (!groups.TryGetValue(provider, out var owned))
                groups[provider] = owned = [];
            owned.Add(file);
        }

        return [.. groups.Select(group => (group.Key, group.Value.ToArray()))];
    }

    private static void Merge(
        Dictionary<string, List<TextEdit>> changes, IReadOnlyDictionary<string, TextEdit[]> edits)
    {
        foreach (var (uri, fileEdits) in edits)
        {
            if (changes.TryGetValue(uri, out var existing))
                existing.AddRange(fileEdits);
            else
                changes[uri] = [.. fileEdits];
        }
    }

    private static bool IsIdentifier(string name) =>
        name.Length > 0 &&
        (char.IsLetter(name[0]) || name[0] == '_') &&
        name.All(c => char.IsLetterOrDigit(c) || c == '_');
}

using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Services.ProjectModel;
using WebFormsCore.Models;
using WebFormsCore.Nodes;

namespace RoslynMCP.Languages.WebForms;

internal sealed partial class WebFormsLanguage : ILanguageFileOperationProvider
{
    /// <summary>
    /// What sits beside a markup file and is named after it. A WebForms page is three files
    /// pretending to be one, and the two that follow are named by appending to the markup's own
    /// full name — <c>Default.aspx.cs</c>, not <c>Default.cs</c>.
    /// </summary>
    private static readonly string[] s_siblingSuffixes = [".cs", ".designer.cs"];

    /// <summary>The directive attributes that name the code-behind file by path.</summary>
    private static readonly string[] s_codeBehindAttributes = ["CodeBehind", "CodeFile"];

    /// <summary>
    /// Renaming <c>Default.aspx</c> to <c>Home.aspx</c> carries the page with it: the code-behind
    /// and designer move alongside, the directive stops naming a file that is gone, and the class
    /// the directive names is renamed everywhere it is used.
    /// </summary>
    /// <remarks>
    /// The moves go in <see cref="WorkspaceEdit.DocumentChanges"/> and the text in
    /// <see cref="WorkspaceEdit.Changes"/>; <see cref="FileOperationsHandler"/> is what orders the
    /// two into a single answer, because only it knows what the C# path and the other packs
    /// contributed.
    /// </remarks>
    public async Task<WorkspaceEdit?> WillRenameAsync(RenameFilesParams p, CancellationToken ct)
    {
        var changes = new Dictionary<string, List<TextEdit>>(StringComparer.OrdinalIgnoreCase);
        var moves = new List<object>();

        foreach (var rename in p.Files)
        {
            ct.ThrowIfCancellationRequested();

            string oldPath = LspConverters.UriToPath(rename.OldUri);
            string newPath = LspConverters.UriToPath(rename.NewUri);

            if (!AspxDocumentService.IsAspxFile(oldPath) || !AspxDocumentService.IsAspxFile(newPath))
                continue;
            if (oldPath.Equals(newPath, StringComparison.OrdinalIgnoreCase))
                continue;

            // Moving the markup into another folder is still a rename to the file system, and the
            // siblings have to follow it there too — only the fixups below need the name to move.
            foreach (string suffix in s_siblingSuffixes)
            {
                if (File.Exists(oldPath + suffix))
                {
                    moves.Add(new RenameFile(
                        LspConverters.PathToUri(oldPath + suffix),
                        LspConverters.PathToUri(newPath + suffix)));

                    await MoveItemAsync(oldPath + suffix, newPath + suffix, ct);
                }
            }

            // The markup last, so the siblings' DependentUpon — which names it — is re-pointed by
            // the same pass that moves the markup's own item.
            await MoveItemAsync(oldPath, newPath, ct);

            await RewriteAsync(oldPath, newPath, changes, ct);
        }

        if (changes.Count == 0 && moves.Count == 0)
            return null;

        return new WorkspaceEdit(
            changes.ToDictionary(c => c.Key, c => c.Value.ToArray(), StringComparer.OrdinalIgnoreCase),
            moves.Count == 0 ? null : [.. moves]);
    }

    /// <summary>
    /// A markup file appeared. Nothing to scaffold, but a file created at a path one was deleted
    /// from would otherwise be served the deleted file's memoized parse.
    /// </summary>
    public Task DidCreateAsync(CreateFilesParams p, CancellationToken ct)
    {
        foreach (var file in p.Files)
            AspxDocumentService.Invalidate(LspConverters.UriToPath(file.Uri));

        return Task.CompletedTask;
    }

    /// <summary>
    /// A markup file was deleted. Its project item goes with it — a legacy WebForms project lists
    /// every page explicitly and one pointing at nothing does not build — and its parse is
    /// dropped. The code-behind and designer are left alone: they were not what the user deleted.
    /// </summary>
    public async Task DidDeleteAsync(DeleteFilesParams p, CancellationToken ct)
    {
        foreach (var file in p.Files)
        {
            ct.ThrowIfCancellationRequested();

            string path = LspConverters.UriToPath(file.Uri);
            if (!AspxDocumentService.IsAspxFile(path))
                continue;

            AspxDocumentService.Invalidate(path);

            try
            {
                await ProjectMutationService.ForgetDeletedFileAsync(path, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ServiceLog.Warn(
                    $"Could not clean up after '{Path.GetFileName(path)}': {ex.Message}",
                    key: $"markup-delete-cleanup-failed:{path}");
            }
        }
    }

    /// <summary>
    /// Carries one of the page's three project items over to the new path.
    /// </summary>
    /// <remarks>
    /// A legacy WebForms project lists every page and its code-behind explicitly, so the items
    /// have to move with the files or the project stops building — the same reason
    /// <see cref="DidDeleteAsync"/> forgets a deleted page. It is done here rather than after the
    /// rename because there is no didRename to do it in, and a failure is logged rather than
    /// raised: the rename itself is still worth performing.
    /// </remarks>
    private static async Task MoveItemAsync(string oldPath, string newPath, CancellationToken ct)
    {
        try
        {
            await ProjectMutationService.RenameFileItemAsync(oldPath, newPath, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ServiceLog.Warn(
                $"Could not move the project item for '{Path.GetFileName(oldPath)}': {ex.Message}",
                key: $"markup-rename-item-failed:{oldPath}");
        }
    }

    /// <summary>
    /// The text a renamed page needs: the directive re-pointed at the moved code-behind, and the
    /// class it names renamed to follow the file.
    /// </summary>
    private static async Task RewriteAsync(
        string oldPath, string newPath,
        Dictionary<string, List<TextEdit>> changes, CancellationToken ct)
    {
        string oldName = Path.GetFileName(oldPath);
        string newName = Path.GetFileName(newPath);
        if (oldName.Equals(newName, StringComparison.Ordinal))
            return;

        // A rename driven from the solution tree has already moved the markup; one driven from the
        // editor has not. The edits are keyed on wherever it is now, which is the path the client
        // will apply them against either way.
        string source = File.Exists(oldPath) ? oldPath : newPath;
        if (await AspxDocumentService.GetAsync(source, ct) is not { Tree: { } root } document)
            return;

        string uri = LspConverters.PathToUri(source);

        foreach (var (name, value) in DirectiveAttributes(root))
        {
            if (s_codeBehindAttributes.Contains(name, StringComparer.OrdinalIgnoreCase)
                && Substitute(document, value, oldName, newName, StringComparison.OrdinalIgnoreCase)
                    is { } edit)
            {
                Add(changes, uri, edit);
            }
        }

        if (document.CodeBehind is not { } codeBehind)
            return;

        string? newTypeName = RenamedClass(
            codeBehind.Name,
            Path.GetFileNameWithoutExtension(oldPath),
            Path.GetFileNameWithoutExtension(newPath));
        if (newTypeName is null)
            return;

        // Two halves of one rename. Roslyn rewrites the declaration and every C# use; the
        // Inherits= that names the class is invisible to it, and is rewritten here rather than
        // through the markup reference pass because that pass replaces the whole attribute value
        // and would drop the namespace qualifying it.
        foreach (var (target, edits) in await FileOperationsHandler.RenameSymbolEditsAsync(
                     codeBehind, document.Project.Solution, newTypeName, ct))
        {
            foreach (var edit in edits)
                Add(changes, target, edit);
        }

        foreach (var (name, value) in DirectiveAttributes(root))
        {
            if (name.Equals("Inherits", StringComparison.OrdinalIgnoreCase)
                && Substitute(document, value, codeBehind.Name, newTypeName, StringComparison.Ordinal)
                    is { } edit)
            {
                Add(changes, uri, edit);
            }
        }
    }

    private static IEnumerable<(string Name, AttributeValue Value)> DirectiveAttributes(RootNode root) =>
        from directive in root.Directives
        from attribute in directive.Attributes
        select (attribute.Key.Value, attribute.Value);

    /// <summary>
    /// Replaces the last occurrence of <paramref name="oldText"/> in a directive attribute's
    /// value, or <c>null</c> when it is not in there. Only that much of the value moves: a
    /// <c>CodeBehind</c> keeps the folder in front of the file name, and an <c>Inherits</c> keeps
    /// the namespace in front of the class.
    /// </summary>
    private static TextEdit? Substitute(
        AspxDocument document, AttributeValue value,
        string oldText, string newText, StringComparison comparison)
    {
        int at = value.Value.LastIndexOf(oldText, comparison);
        if (at < 0)
            return null;

        return new TextEdit(
            LspConverters.ToRange(document.SourceText.Lines, AspxSymbolResolver.Span(value.Range)),
            string.Concat(
                value.Value.AsSpan(0, at), newText, value.Value.AsSpan(at + oldText.Length)));
    }

    /// <summary>
    /// The code-behind class's new name, or <c>null</c> when its name never followed the file's.
    /// </summary>
    /// <remarks>
    /// A page class is conventionally the file's stem plus decoration — <c>Designer.aspx</c>
    /// declares <c>DesignerPage</c>, <c>Default.aspx</c> declares <c>_Default</c> — so the stem is
    /// substituted and whatever surrounds it is left alone. A class named after something else was
    /// not named after the file, and renaming it is not what dragging the file asked for.
    /// </remarks>
    private static string? RenamedClass(string className, string oldStem, string newStem)
    {
        if (oldStem.Length == 0 || !IsIdentifier(newStem))
            return null;

        int lead = 0;
        while (lead < className.Length && className[lead] == '_')
            lead++;

        if (!className.AsSpan(lead).StartsWith(oldStem, StringComparison.OrdinalIgnoreCase))
            return null;

        string renamed = string.Concat(
            className.AsSpan(0, lead), newStem, className.AsSpan(lead + oldStem.Length));

        return renamed.Equals(className, StringComparison.Ordinal) ? null : renamed;
    }

    private static void Add(
        Dictionary<string, List<TextEdit>> changes, string uri, TextEdit edit)
    {
        if (!changes.TryGetValue(uri, out var edits))
            changes[uri] = edits = [];
        edits.Add(edit);
    }

    private static bool IsIdentifier(string name) =>
        name.Length > 0 &&
        (char.IsLetter(name[0]) || name[0] == '_') &&
        name.All(c => char.IsLetterOrDigit(c) || c == '_');
}

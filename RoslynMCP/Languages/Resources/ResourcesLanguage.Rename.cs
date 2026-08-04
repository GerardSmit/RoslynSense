using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Rename;
using RoslynMCP.Languages.Resources.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Tools;

namespace RoslynMCP.Languages.Resources;

/// <summary>
/// Renaming a resource key: the <c>name=</c> attribute in every file of the family, every call site
/// that reads it in C# or markup, and — where a strongly-typed wrapper exists — the generated
/// property and everything that goes through it.
/// </summary>
internal sealed partial class ResourcesLanguage : ISymbolFreeRenameProvider, ILanguageRenameProvider
{
    public async Task<PrepareRenameResult?> PrepareAsync(
        string filePath, int offset, CancellationToken ct)
    {
        if (await ResourceKeySearch.LocateAsync(Settings, filePath, offset, project: null, ct)
            is not { } target)
        {
            return null;
        }

        // Anything past Inferred means the file set was guessed rather than resolved, and a rename
        // applied to a guessed set is silent corruption. Declining here is what makes offering the
        // gesture at all defensible.
        if (target.Confidence > RootConfidence.Inferred || !Spannable(target))
            return null;

        return new PrepareRenameResult(
            LspConverters.ToRange(target.Text.Lines, target.Span), target.Written);
    }

    public async Task<WorkspaceEdit?> RenameAsync(
        string filePath, int offset, string newName, Project? project, CancellationToken ct)
    {
        project ??= await ProjectOfAsync(filePath, ct);

        if (await ResourceKeySearch.LocateAsync(Settings, filePath, offset, project, ct)
            is not { } target || target.Confidence > RootConfidence.Inferred)
        {
            return null;
        }

        // The name the user typed is in the caret site's own form, so renaming from a DNN call that
        // abbreviates `Save.Text` to `"Save"` yields the entry `Save.Text` again.
        string newKey = ResourceKeySearch.EffectiveKey(newName, target.KeySuffix);
        var (sites, complete) = await ResourceKeySearch.CollectAsync(Settings, target, ct);

        if (!complete || sites.IsEmpty)
            return null;

        var changes = new Dictionary<string, List<TextEdit>>(StringComparer.OrdinalIgnoreCase);

        void Add(string uri, TextEdit edit)
        {
            if (!changes.TryGetValue(uri, out var list))
                changes[uri] = list = [];
            if (!list.Contains(edit))
                list.Add(edit);
        }

        foreach (var site in sites)
        {
            Add(
                LspConverters.PathToUri(site.FilePath),
                new TextEdit(
                    LspConverters.ToRange(site.Text.Lines, site.Span),
                    ResourceKeySearch.WrittenForm(newKey, site.KeySuffix)));
        }

        await AddWrapperEditsAsync(target, newKey, Add, ct);

        // DocumentChanges stays null. A client that understands it ignores `changes` entirely, and
        // a key rename creates, moves and deletes nothing — the case that needs the ordered form is
        // renaming the .resx itself, which is a file operation rather than this.
        return changes.Count == 0
            ? null
            : new WorkspaceEdit(changes.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
    }

    // ---- The .resx buffer itself ----------------------------------------------------------------

    public async Task<PrepareRenameResult?> PrepareRenameAsync(
        TextDocumentPositionParams p, CancellationToken ct) =>
        Caret(p.TextDocument, p.Position) is { } at
            ? await PrepareAsync(at.Path, at.Offset, ct)
            : null;

    public async Task<WorkspaceEdit?> RenameAsync(RenameParams p, CancellationToken ct) =>
        Caret(p.TextDocument, p.Position) is { } at
            ? await RenameAsync(at.Path, at.Offset, p.NewName, project: null, ct)
            : null;

    /// <summary>
    /// A position in one of the pack's own files, as a path and an offset. Shared with the
    /// find-references half: a <c>.resx</c> is not a Roslyn document, so the handlers' usual resolve
    /// never reaches it and the buffer is read here instead.
    /// </summary>
    private static (string Path, int Offset)? Caret(
        TextDocumentIdentifier document, Position position)
    {
        string path = LspConverters.UriToPath(document.Uri);

        return ResourceCatalogService.Text(path) is { } text
            ? (path, LspConverters.ToOffset(text, position))
            : null;
    }

    /// <summary>The project a file belongs to, for a request that carried none.</summary>
    private static async Task<Project?> ProjectOfAsync(string filePath, CancellationToken ct)
    {
        if (await NonCSharpProjectFinder.FindProjectAsync(filePath, ct) is not { Length: > 0 } projectPath)
            return null;

        try
        {
            var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
                projectPath, targetFilePath: filePath, cancellationToken: ct);
            return project;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ServiceLog.Warn(
                $"Could not open '{Path.GetFileName(projectPath)}' for a resource key: {ex.Message}",
                key: $"resource-project:{projectPath}");
            return null;
        }
    }

    // ---- The strongly-typed wrapper -------------------------------------------------------------

    /// <summary>
    /// The generated wrapper's half of the rename.
    /// </summary>
    /// <remarks>
    /// <c>public static string Title</c> in a <c>Strings.Designer.cs</c> <em>is</em> a real symbol,
    /// so its call sites go through <see cref="Renamer"/> — the only correct way to reach
    /// <c>Resources.Title</c>, where a text search for <c>Title</c> would hit everything. The edits
    /// merge into the same table as the rest, so the property and the literal in the body beside it
    /// cannot end up disagreeing.
    /// </remarks>
    private static async Task AddWrapperEditsAsync(
        ResourceKeyTarget target, string newKey, Action<string, TextEdit> add, CancellationToken ct)
    {
        if (target.Project is not { } project)
            return;

        var original = project.Solution;
        var renamed = original;

        foreach (var family in target.Families)
        {
            string path = Path.Combine(family.Directory, family.BaseName + ".Designer.cs");

            var designer = original.Projects
                .SelectMany(p => p.Documents)
                .FirstOrDefault(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase));

            if (designer is null)
                continue;

            foreach (var (before, after) in RenamedEntries(target, newKey))
            {
                ct.ThrowIfCancellationRequested();

                string from = Identifier(before);
                string to = Identifier(after);

                if (from == to || !SyntaxFacts.IsValidIdentifier(to))
                    continue;

                if (renamed.GetDocument(designer.Id) is not { } current
                    || await PropertyAsync(current, from, ct) is not { } property)
                {
                    continue;
                }

                renamed = await Renamer.RenameSymbolAsync(
                    renamed, property, new SymbolRenameOptions(), to, ct);
            }
        }

        if (ReferenceEquals(renamed, original))
            return;

        foreach (var change in renamed.GetChanges(original).GetProjectChanges())
        {
            foreach (var id in change.GetChangedDocuments())
            {
                var before = original.GetDocument(id);
                var after = renamed.GetDocument(id);

                if (before?.FilePath is not { Length: > 0 } path || after is null)
                    continue;

                var text = await before.GetTextAsync(ct);

                foreach (var edit in await after.GetTextChangesAsync(before, ct))
                {
                    add(
                        LspConverters.PathToUri(path),
                        new TextEdit(LspConverters.ToRange(text.Lines, edit.Span), edit.NewText ?? ""));
                }
            }
        }
    }

    private static async Task<IPropertySymbol?> PropertyAsync(
        Document document, string name, CancellationToken ct)
    {
        if (await document.GetSyntaxRootAsync(ct) is not { } root)
            return null;

        var declaration = root.DescendantNodes()
            .OfType<PropertyDeclarationSyntax>()
            .FirstOrDefault(p => p.Identifier.ValueText == name);

        if (declaration is null || await document.GetSemanticModelAsync(ct) is not { } model)
            return null;

        return model.GetDeclaredSymbol(declaration, ct);
    }

    /// <summary>Every entry the rename moves. A key names one; a <c>meta:resourcekey</c> group names
    /// the whole <c>btnSave.*</c> set, and each of those keeps its own tail.</summary>
    private static IEnumerable<(string Before, string After)> RenamedEntries(
        ResourceKeyTarget target, string newKey)
    {
        if (!target.Group)
        {
            yield return (target.Key, newKey);
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var family in target.Families)
        {
            foreach (string key in family.AllKeys)
            {
                if (ResourceKeySearch.InGroup(key, target.Key) && seen.Add(key))
                    yield return (key, newKey + key[target.Key.Length..]);
            }
        }
    }

    /// <summary>The property name the resx code generator makes from a key: anything that cannot
    /// appear in an identifier becomes an underscore, and a name that cannot start one is
    /// prefixed.</summary>
    private static string Identifier(string key)
    {
        var builder = new StringBuilder(key.Length + 1);

        foreach (char c in key)
            builder.Append(SyntaxFacts.IsIdentifierPartCharacter(c) ? c : '_');

        if (builder.Length > 0 && !SyntaxFacts.IsIdentifierStartCharacter(builder[0]))
            builder.Insert(0, '_');

        return builder.ToString();
    }

    /// <summary>
    /// Whether every declaration the rename would move has an exact range. A key carrying an entity
    /// reference comes back from the reader unspanned, and rewriting its call sites while leaving
    /// the declaration behind is worse than declining.
    /// </summary>
    private static bool Spannable(ResourceKeyTarget target)
    {
        foreach (var family in target.Families)
        {
            foreach (var file in family.Files)
            {
                foreach (var entry in file.Entries.Values)
                {
                    if (ResourceKeySearch.Covers(target, entry.Key)
                        && entry.KeySpan.Length != entry.Key.Length)
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }
}

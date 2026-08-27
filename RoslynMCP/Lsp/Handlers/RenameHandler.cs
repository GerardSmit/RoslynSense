using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using RoslynMCP.Languages;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>prepareRename / rename. Returns a <see cref="WorkspaceEdit"/> — the editor applies
/// it to its buffers; the server NEVER writes renamed files to disk (the user may have unsaved
/// edits, and undo must stay in the editor).</summary>
internal static class RenameHandler
{
    public static async Task<PrepareRenameResult?> PrepareRenameAsync(
        TextDocumentPositionParams p, CancellationToken ct, LanguageSession? languages = null)
    {
        if (await HandlerHelpers.ResolveAsync(p.TextDocument, p.Position, ct) is not
            var (document, text, offset) || document is null)
            return null;

        string path = LspConverters.UriToPath(p.TextDocument.Uri);

        // Before the symbol lookup, never after: a caret inside a string literal binds to nothing,
        // so by the time a contributor would be reached this method has already returned null. What
        // these providers rename is not an ISymbol at all — a resource key has no declaration
        // Roslyn can bind to, and the pack that owns it performs the whole rename rather than
        // adding edits to one Roslyn is already performing.
        foreach (var provider in
                 LanguageScope.Of(languages).Contributors<ISymbolFreeRenameProvider>())
        {
            if (await provider.PrepareAsync(path, offset, ct) is { } prepared)
                return prepared;
        }

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, offset, ct);
        if (symbol is null || symbol.Locations.All(l => !l.IsInSource))
            return null; // metadata symbols can't be renamed

        // The name, not whatever token the caret's offset happens to land in: with the caret at
        // the end of a name the offset belongs to the token after it, and answering with that one
        // opens the rename box over a paren, prefilled with it.
        var root = await document.GetSyntaxRootAsync(ct);
        if (root is null || CaretTokens.Touching(root, offset, IsNameToken) is not { } t)
            return null;

        return new PrepareRenameResult(LspConverters.ToRange(text.Lines, t.Span), t.ValueText);
    }

    /// <summary>
    /// What a rename can be anchored to. Contextual keywords bind as identifiers, so this is a
    /// kind check rather than a list of words.
    /// </summary>
    private static bool IsNameToken(SyntaxToken token) =>
        token.IsKind(SyntaxKind.IdentifierToken);

    public static async Task<WorkspaceEdit?> RenameAsync(
        RenameParams p, CancellationToken ct, LanguageSession? languages = null)
    {
        if (await HandlerHelpers.ResolveAsync(p.TextDocument, p.Position, ct) is not
            var (document, _, offset) || document is null)
            return null;

        string filePath = LspConverters.UriToPath(p.TextDocument.Uri);

        // Ahead of the symbol lookup for the same reason prepareRename is.
        foreach (var provider in
                 LanguageScope.Of(languages).Contributors<ISymbolFreeRenameProvider>())
        {
            if (await provider.RenameAsync(filePath, offset, p.NewName, document.Project, ct) is { } edit)
                return edit;
        }

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, offset, ct);
        if (symbol is null || symbol.Locations.All(l => !l.IsInSource))
            return null;

        var solution = document.Project.Solution;
        var renamed = await Renamer.RenameSymbolAsync(
            solution, symbol, new SymbolRenameOptions(), p.NewName, ct);

        var changes = new Dictionary<string, List<TextEdit>>(StringComparer.OrdinalIgnoreCase);

        void Add(string uri, TextEdit edit)
        {
            if (!changes.TryGetValue(uri, out var list))
                changes[uri] = list = [];
            if (!list.Contains(edit))
                list.Add(edit);
        }

        foreach (var projectChange in renamed.GetChanges(solution).GetProjectChanges())
        {
            foreach (var docId in projectChange.GetChangedDocuments())
            {
                var oldDoc = solution.GetDocument(docId);
                var newDoc = renamed.GetDocument(docId);
                if (oldDoc?.FilePath is not { Length: > 0 } path || newDoc is null)
                    continue;

                var oldText = await oldDoc.GetTextAsync(ct);
                foreach (var c in await newDoc.GetTextChangesAsync(oldDoc, ct))
                {
                    Add(LspConverters.PathToUri(path),
                        new TextEdit(LspConverters.ToRange(oldText.Lines, c.Span), c.NewText ?? ""));
                }
            }
        }

        // The enabled packs' edits, for the same reason AllReferencesAsync asks them: an OnClick=
        // naming this method is a reference Roslyn cannot see, and a rename that skips it leaves
        // the attribute pointing at a method that no longer exists. On a project with no markup a
        // contributor declines after one metadata lookup.
        foreach (var contributor in LanguageScope.Of(languages).Contributors<ILanguageRenameContributor>())
        {
            foreach (var (uri, edit) in
                     await contributor.RenameEditsAsync(symbol, document.Project, p.NewName, ct))
            {
                Add(uri, edit);
            }
        }

        return changes.Count == 0
            ? null
            : new WorkspaceEdit(changes.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
    }
}

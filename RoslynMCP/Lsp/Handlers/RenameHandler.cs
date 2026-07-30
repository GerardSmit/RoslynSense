using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>prepareRename / rename. Returns a <see cref="WorkspaceEdit"/> — the editor applies
/// it to its buffers; the server NEVER writes renamed files to disk (the user may have unsaved
/// edits, and undo must stay in the editor).</summary>
internal static class RenameHandler
{
    public static async Task<PrepareRenameResult?> PrepareRenameAsync(
        TextDocumentPositionParams p, CancellationToken ct)
    {
        if (await HandlerHelpers.ResolveAsync(p.TextDocument, p.Position, ct) is not
            var (document, text, offset) || document is null)
            return null;

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, offset, ct);
        if (symbol is null || symbol.Locations.All(l => !l.IsInSource))
            return null; // metadata symbols can't be renamed

        var root = await document.GetSyntaxRootAsync(ct);
        var token = root?.FindToken(offset);
        if (token is not { } t || !t.Span.Contains(offset))
            return null;

        return new PrepareRenameResult(LspConverters.ToRange(text.Lines, t.Span), t.ValueText);
    }

    public static async Task<WorkspaceEdit?> RenameAsync(RenameParams p, CancellationToken ct)
    {
        if (await HandlerHelpers.ResolveAsync(p.TextDocument, p.Position, ct) is not
            var (document, _, offset) || document is null)
            return null;

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, offset, ct);
        if (symbol is null || symbol.Locations.All(l => !l.IsInSource))
            return null;

        var solution = document.Project.Solution;
        var renamed = await Renamer.RenameSymbolAsync(
            solution, symbol, new SymbolRenameOptions(), p.NewName, ct);

        var changes = new Dictionary<string, TextEdit[]>();
        foreach (var projectChange in renamed.GetChanges(solution).GetProjectChanges())
        {
            foreach (var docId in projectChange.GetChangedDocuments())
            {
                var oldDoc = solution.GetDocument(docId);
                var newDoc = renamed.GetDocument(docId);
                if (oldDoc?.FilePath is not { Length: > 0 } path || newDoc is null)
                    continue;

                var oldText = await oldDoc.GetTextAsync(ct);
                var textChanges = await newDoc.GetTextChangesAsync(oldDoc, ct);
                var edits = textChanges
                    .Select(c => new TextEdit(LspConverters.ToRange(oldText.Lines, c.Span), c.NewText ?? ""))
                    .ToArray();
                if (edits.Length > 0)
                    changes[LspConverters.PathToUri(path)] = edits;
            }
        }

        return changes.Count == 0 ? null : new WorkspaceEdit(changes);
    }
}

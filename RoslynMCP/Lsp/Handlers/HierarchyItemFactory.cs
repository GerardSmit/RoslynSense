using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>Builds LSP hierarchy items from symbols and resolves them back. Items carry no
/// opaque data: SelectionRange points at the declaration identifier, so re-resolving the
/// symbol is a FindSymbolAtPositionAsync at Uri + SelectionRange.Start.</summary>
internal static class HierarchyItemFactory
{
    public static HierarchyItem? ToItem(ISymbol symbol)
    {
        var identifierLocation = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (identifierLocation?.SourceTree?.FilePath is not { Length: > 0 } path)
            return null;

        // Full range: the declaring syntax node in the same tree; falls back to the identifier.
        var declaration = symbol.DeclaringSyntaxReferences
            .FirstOrDefault(r => r.SyntaxTree == identifierLocation.SourceTree);
        var fullSpan = declaration?.SyntaxTree.GetLocation(declaration.Span).GetLineSpan().Span
            ?? identifierLocation.GetLineSpan().Span;

        return new HierarchyItem(
            symbol.Name.Length > 0 ? symbol.Name : symbol.ToDisplayString(),
            LspConverters.ToLspSymbolKind(symbol),
            LspConverters.PathToUri(path),
            LspConverters.ToRange(fullSpan),
            LspConverters.ToRange(identifierLocation.GetLineSpan().Span),
            symbol.ContainingType?.ToDisplayString() ?? symbol.ContainingNamespace?.ToDisplayString());
    }

    public static async Task<(ISymbol? Symbol, Document? Document)> ResolveSymbolAsync(
        HierarchyItem item, CancellationToken ct)
    {
        var resolved = await HandlerHelpers.ResolveAsync(
            new TextDocumentIdentifier(item.Uri), item.SelectionRange.Start, ct);
        if (resolved is not var (document, _, offset))
            return (null, null);

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, offset, ct);
        return (symbol, document);
    }
}

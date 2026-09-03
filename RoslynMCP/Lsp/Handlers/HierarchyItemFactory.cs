using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>
/// Maps a position in generated C# back to the file it was generated from.
/// </summary>
/// <remarks>
/// A language pack that answers hierarchy questions projects its own files into C# and hands
/// Roslyn the result, so the symbols that come back are declared in a document that exists only
/// in memory. Every URI in a hierarchy response is one the editor is expected to open, so a
/// result that cannot be mapped back is dropped rather than reported against the projection.
/// </remarks>
internal interface IHierarchySourceMapper
{
    /// <summary>Whether this file is generated rather than one the user can open.</summary>
    bool IsGenerated(string? filePath);

    /// <summary>The real file and range a generated span came from, or <c>null</c> when it landed
    /// in scaffolding the generator invented and corresponds to nothing in the source.</summary>
    (string Uri, Protocol.Range Range)? ToSource(string filePath, TextSpan span);
}

/// <summary>Builds LSP hierarchy items from symbols and resolves them back. Items carry no
/// opaque data: SelectionRange points at the declaration identifier, so re-resolving the
/// symbol is a FindSymbolAtPositionAsync at Uri + SelectionRange.Start.</summary>
internal static class HierarchyItemFactory
{
    public static HierarchyItem? ToItem(ISymbol symbol, IHierarchySourceMapper? mapper = null)
    {
        // A symbol declared in a real file and in a pack's projection both — a page's partial
        // class is — is shown where the user can already open it. Only one that exists nowhere
        // else is worth mapping back through the projection.
        var declarations = symbol.Locations
            .Where(l => l.IsInSource && l.SourceTree?.FilePath is { Length: > 0 })
            .OrderBy(l => mapper?.IsGenerated(l.SourceTree!.FilePath) == true ? 1 : 0);

        foreach (var location in declarations)
        {
            string path = location.SourceTree!.FilePath;

            // Full range: the declaring syntax node in the same tree; falls back to the identifier.
            var declaration = symbol.DeclaringSyntaxReferences
                .FirstOrDefault(r => r.SyntaxTree == location.SourceTree);

            if (mapper?.IsGenerated(path) == true)
            {
                if (mapper.ToSource(path, location.SourceSpan) is not { } identifier)
                    continue;

                var mappedFull = declaration is null
                    ? identifier.Range
                    : mapper.ToSource(path, declaration.Span)?.Range ?? identifier.Range;

                return At(symbol, identifier.Uri, mappedFull, identifier.Range);
            }

            var fullSpan = declaration?.SyntaxTree.GetLocation(declaration.Span).GetLineSpan().Span
                ?? location.GetLineSpan().Span;

            return At(
                symbol,
                LspConverters.PathToUri(path),
                LspConverters.ToRange(fullSpan),
                LspConverters.ToRange(location.GetLineSpan().Span));
        }

        return null;
    }

    /// <summary>
    /// An item for a symbol anchored somewhere other than its own declaration — where a language
    /// pack's file names it, when the declaration itself is generated text with nothing in the
    /// real file to point at.
    /// </summary>
    public static HierarchyItem At(
        ISymbol symbol, string uri, Protocol.Range range, Protocol.Range selectionRange) =>
        new(symbol.Name.Length > 0 ? symbol.Name : symbol.ToDisplayString(),
            LspConverters.ToLspSymbolKind(symbol),
            uri,
            range,
            selectionRange,
            symbol.ContainingType?.ToDisplayString() ?? symbol.ContainingNamespace?.ToDisplayString());

    /// <summary>
    /// One source location as a URI and a range, mapped out of a projection when it is in one.
    /// Returns <c>null</c> for a location the client could not act on: a generated span that maps
    /// to nothing, or a tree with no path.
    /// </summary>
    public static (string Uri, Protocol.Range Range)? ToSource(
        Microsoft.CodeAnalysis.Location location, IHierarchySourceMapper? mapper)
    {
        if (location.SourceTree?.FilePath is not { Length: > 0 } path)
            return null;

        return mapper?.IsGenerated(path) == true
            ? mapper.ToSource(path, location.SourceSpan)
            : (LspConverters.PathToUri(path), LspConverters.ToRange(location.GetLineSpan().Span));
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

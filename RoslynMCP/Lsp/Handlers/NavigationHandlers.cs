using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMCP.Lsp.Protocol;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>definition / typeDefinition / references / implementation / documentHighlight.</summary>
internal static class NavigationHandlers
{
    public static async Task<LspLocation[]> DefinitionAsync(
        TextDocumentPositionParams p, bool typeDefinition, CancellationToken ct)
    {
        if (await HandlerHelpers.ResolveAsync(p.TextDocument, p.Position, ct) is not
            var (document, _, offset) || document is null)
            return Array.Empty<LspLocation>();

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, offset, ct);
        if (symbol is null)
            return Array.Empty<LspLocation>();

        if (typeDefinition)
        {
            symbol = symbol switch
            {
                ILocalSymbol l => l.Type,
                IParameterSymbol pa => pa.Type,
                IFieldSymbol f => f.Type,
                IPropertySymbol pr => pr.Type,
                IEventSymbol ev => ev.Type,
                IMethodSymbol m => m.ReturnType,
                _ => symbol,
            };
        }

        // Aliases and partials: prefer the definition part(s) in source.
        symbol = symbol.OriginalDefinition;
        return HandlerHelpers.ToLocations(symbol.Locations);
    }

    public static async Task<LspLocation[]> ReferencesAsync(ReferenceParams p, CancellationToken ct)
    {
        if (await HandlerHelpers.ResolveAsync(p.TextDocument, p.Position, ct) is not
            var (document, _, offset) || document is null)
            return Array.Empty<LspLocation>();

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, offset, ct);
        if (symbol is null)
            return Array.Empty<LspLocation>();

        var references = await SymbolFinder.FindReferencesAsync(symbol, document.Project.Solution, ct);

        var locations = new List<Microsoft.CodeAnalysis.Location>();
        foreach (var referenced in references)
        {
            if (p.Context.IncludeDeclaration)
                locations.AddRange(referenced.Definition.Locations.Where(l => l.IsInSource));
            locations.AddRange(referenced.Locations.Select(r => r.Location));
        }

        return HandlerHelpers.ToLocations(locations);
    }

    public static async Task<LspLocation[]> ImplementationAsync(TextDocumentPositionParams p, CancellationToken ct)
    {
        if (await HandlerHelpers.ResolveAsync(p.TextDocument, p.Position, ct) is not
            var (document, _, offset) || document is null)
            return Array.Empty<LspLocation>();

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, offset, ct);
        if (symbol is null)
            return Array.Empty<LspLocation>();

        var solution = document.Project.Solution;
        var results = new List<ISymbol>();

        switch (symbol)
        {
            case INamedTypeSymbol { TypeKind: TypeKind.Interface } iface:
                results.AddRange(await SymbolFinder.FindImplementationsAsync(iface, solution, cancellationToken: ct));
                break;
            case INamedTypeSymbol { IsAbstract: true } abstractType:
                results.AddRange(await SymbolFinder.FindDerivedClassesAsync(abstractType, solution, cancellationToken: ct));
                break;
            case INamedTypeSymbol namedType:
                results.AddRange(await SymbolFinder.FindDerivedClassesAsync(namedType, solution, cancellationToken: ct));
                break;
            default:
                results.AddRange(await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: ct));
                results.AddRange(await SymbolFinder.FindOverridesAsync(symbol, solution, cancellationToken: ct));
                break;
        }

        if (results.Count == 0)
            results.Add(symbol); // e.g. invoking on a concrete member — jump to it

        return HandlerHelpers.ToLocations(results.SelectMany(s => s.Locations).Where(l => l.IsInSource));
    }

    public static async Task<DocumentHighlight[]> DocumentHighlightAsync(
        TextDocumentPositionParams p, CancellationToken ct)
    {
        if (await HandlerHelpers.ResolveAsync(p.TextDocument, p.Position, ct) is not
            var (document, text, offset) || document is null)
            return Array.Empty<DocumentHighlight>();

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, offset, ct);
        if (symbol is null)
            return Array.Empty<DocumentHighlight>();

        // Same-file scope only: pass just this document to the reference search.
        var references = await SymbolFinder.FindReferencesAsync(
            symbol, document.Project.Solution, ImmutableHashSet.Create(document), ct);

        var highlights = new List<DocumentHighlight>();
        foreach (var referenced in references)
        {
            foreach (var loc in referenced.Definition.Locations)
            {
                if (loc.IsInSource && loc.SourceTree == await document.GetSyntaxTreeAsync(ct))
                    highlights.Add(new DocumentHighlight(LspConverters.ToRange(loc.GetLineSpan().Span), 1));
            }
            foreach (var refLoc in referenced.Locations)
            {
                if (refLoc.Document.Id == document.Id)
                    highlights.Add(new DocumentHighlight(
                        LspConverters.ToRange(text.Lines, refLoc.Location.SourceSpan), 2));
            }
        }

        return highlights.DistinctBy(h => h.Range).ToArray();
    }
}

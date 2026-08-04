using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMCP.Languages;
using RoslynMCP.Lsp.Protocol;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>definition / typeDefinition / references / implementation / documentHighlight.</summary>
internal static class NavigationHandlers
{
    public static async Task<LspLocation[]> DefinitionAsync(
        TextDocumentPositionParams p, bool typeDefinition, CancellationToken ct,
        LanguageSession? languages = null)
    {
        if (await HandlerHelpers.ResolveAsync(p.TextDocument, p.Position, ct) is not
            var (document, _, offset) || document is null)
            return Array.Empty<LspLocation>();

        // Inside a string literal Roslyn binds to nothing, so a route template's "{id}" would
        // navigate nowhere. Ask the embedded languages first; the check ends after a syntax
        // lookup unless the caret really is in a literal, and before that when none are
        // registered.
        if (await RoslynEmbeddedLanguages.Current.DetectAsync(document, offset, ct) is
            { Language: IEmbeddedDefinitionProvider embedded } embeddedContext)
        {
            return await embedded.DefinitionAsync(embeddedContext, typeDefinition, ct);
        }

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, offset, ct);
        if (symbol is null)
            return Array.Empty<LspLocation>();

        // A dispatcher is not a destination. The caret on a mediator's Send binds to the one
        // interface member every send in the solution binds to, so Roslyn's answer is the same
        // wherever it is asked from and is never what was wanted; which handler runs is decided by
        // the argument, which is why the redirect gets the position and not just the symbol.
        // Nothing recognising the caret is the normal case and leaves everything below unchanged.
        var kind = typeDefinition ? NavigationKind.TypeDefinition : NavigationKind.Definition;
        var redirected = await RedirectedSymbolsAsync(document, offset, symbol, kind, languages, ct);

        var locations = new List<LspLocation>();

        if (redirected.Count > 0)
        {
            // typeDefinition is deliberately not passed on. The redirect has already chosen
            // between the handler and the type declaring it, and DefinitionLocationsAsync would
            // map the method it named to that method's return type.
            foreach (var target in redirected)
            {
                locations.AddRange(
                    await DefinitionLocationsAsync(target, document.Project, typeDefinition: false, ct));
            }
        }
        else
        {
            locations.AddRange(
                await DefinitionLocationsAsync(symbol, document.Project, typeDefinition, ct));
        }

        // The same both-must-run case as AllReferencesAsync, from the other end: Roslyn answers
        // with the C# declaration and the pack that generated it answers with the line it was
        // generated from. Contributors are asked about the symbol the request is really about —
        // for typeDefinition that is the type, not the variable holding it, which is what makes
        // Ctrl+F12 on a generated type reach its source, and after a redirect it is what the
        // redirect named, so a handler protoc generated still reaches the .proto line behind it.
        var subjects = redirected.Count > 0
            ? redirected
            : (IReadOnlyList<ISymbol>)[typeDefinition ? TypeOf(symbol) : symbol];

        var contributed = new List<LspLocation>();
        var answered = new List<ILanguageDefinitionContributor>();

        foreach (var contributor in
                 LanguageScope.Of(languages).Contributors<ILanguageDefinitionContributor>())
        {
            int before = contributed.Count;

            foreach (var subject in subjects)
                contributed.AddRange(await contributor.DefinitionsAsync(subject, document.Project, ct));

            // Only a pack that put something in this answer may take something out of it, which is
            // what keeps a decline from being able to empty the result.
            if (contributed.Count > before)
                answered.Add(contributor);
        }

        // A generated declaration is not an alternative to the line it was generated from — it is
        // that line, re-emitted into a file the next build overwrites. Leaving it in makes F12 a
        // pick-one-of-two, so it goes rather than merely ranking second.
        if (answered.Count > 0)
            locations.RemoveAll(location => answered.Any(pack => pack.Supersedes(location)));

        locations.AddRange(contributed);
        return locations.Distinct().ToArray();
    }

    /// <summary>
    /// What the enabled packs say this caret really points at, in place of the symbol Roslyn bound,
    /// or nothing when none of them recognises it.
    /// </summary>
    /// <remarks>
    /// Internal rather than private because the MCP <c>go_to_definition_snippet</c> tool calls it
    /// with the document and offset its own markup resolution produced. One helper for both
    /// front-ends is what stops the editor and an AI session resolving the same dispatch
    /// differently.
    /// </remarks>
    internal static async Task<IReadOnlyList<ISymbol>> RedirectedSymbolsAsync(
        Document document, int offset, ISymbol symbol, NavigationKind kind,
        LanguageSession? languages, CancellationToken ct)
    {
        var redirectors = LanguageScope.Of(languages).Contributors<ILanguageDefinitionRedirector>();
        if (redirectors.Count == 0)
            return [];

        var context = new NavigationContext(document, offset, symbol, kind);
        var results = new List<ISymbol>();

        foreach (var redirector in redirectors)
            results.AddRange(await redirector.RedirectAsync(context, ct));

        return results;
    }

    /// <summary>
    /// Where a symbol is defined, with the fallbacks that make navigation land somewhere useful
    /// for a symbol that has no source in the solution. Shared with the markup languages, whose
    /// symbols come from a parse tree rather than from a syntax position.
    /// </summary>
    public static async Task<LspLocation[]> DefinitionLocationsAsync(
        ISymbol symbol, Project project, bool typeDefinition, CancellationToken ct)
    {
        if (typeDefinition)
            symbol = TypeOf(symbol);

        // Aliases and partials: prefer the definition part(s) in source.
        symbol = symbol.OriginalDefinition;
        var locations = await HandlerHelpers.ToLocationsAsync(symbol.Locations, project, ct);
        if (locations.Length > 0)
            return locations;

        // Metadata symbol (framework/package type). Its own source first, if the assembly says
        // where to get it: Source Link gives the file the author wrote, comments and all, where
        // decompilation gives a faithful but stripped reconstruction of it.
        if (await Services.SourceLinkService.TryResolveAsync(symbol, project, ct) is { } linked)
        {
            var line = Math.Max(0, linked.Line - 1);
            return
            [
                new LspLocation(
                    LspConverters.PathToUri(linked.FilePath),
                    new Protocol.Range(new Position(line, 0), new Position(line, 0))),
            ];
        }

        var decompiled = await Services.DecompiledSourceService.TryDecompileSymbolAsync(
            symbol, project, ct);
        var location = decompiled?.Locations.FirstOrDefault(l => l.IsInSource);
        return location is not null && LspConverters.ToLocation(location) is { } lsp
            ? [lsp]
            : Array.Empty<LspLocation>();
    }

    /// <summary>
    /// What <c>textDocument/typeDefinition</c> is actually asking about: the type of the thing
    /// under the caret, or the thing itself when it has no type of its own. Applying it twice
    /// changes nothing, since none of the mapped results is a local, a parameter, a field, a
    /// property, an event or a method.
    /// </summary>
    private static ISymbol TypeOf(ISymbol symbol) => symbol switch
    {
        ILocalSymbol l => l.Type,
        IParameterSymbol pa => pa.Type,
        IFieldSymbol f => f.Type,
        IPropertySymbol pr => pr.Type,
        IEventSymbol ev => ev.Type,
        IMethodSymbol m => m.ReturnType,
        _ => symbol,
    };

    public static async Task<LspLocation[]> ReferencesAsync(
        ReferenceParams p, CancellationToken ct, LanguageSession? languages = null)
    {
        if (await HandlerHelpers.ResolveAsync(p.TextDocument, p.Position, ct) is not
            var (document, _, offset) || document is null)
            return Array.Empty<LspLocation>();

        // Ahead of the symbol lookup, like the embedded-language branch in DefinitionAsync and for
        // the same reason: what a resource key in a string literal binds to is nothing, and the
        // search that can answer it is the pack's, not Roslyn's.
        foreach (var provider in
                 LanguageScope.Of(languages).Contributors<ISymbolFreeReferenceProvider>())
        {
            if (await provider.ReferencesAsync(
                    LspConverters.UriToPath(p.TextDocument.Uri), offset, document.Project, ct)
                is { } found)
            {
                return [.. found];
            }
        }

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, offset, ct);
        if (symbol is null)
            return Array.Empty<LspLocation>();

        return await AllReferencesAsync(
            symbol, document.Project, p.Context.IncludeDeclaration, ct, languages);
    }

    /// <summary>
    /// A symbol's references, in C# and in the enabled packs' files.
    /// </summary>
    /// <remarks>
    /// The one place a request is not an either/or: the <c>OnClick=</c> in an <c>.aspx</c> that
    /// names a handler is a reference to it, and Roslyn cannot see it, so find-references started
    /// in the code-behind has to ask the packs too. Shared with the markup handlers so a search
    /// started from either side gives the same answer. On a solution with no markup each
    /// contributor declines after one metadata lookup.
    /// </remarks>
    public static async Task<LspLocation[]> AllReferencesAsync(
        ISymbol symbol, Project project, bool includeDeclaration, CancellationToken ct,
        LanguageSession? languages = null)
    {
        var locations = new List<Microsoft.CodeAnalysis.Location>();

        foreach (var referenced in await SymbolFinder.FindReferencesAsync(symbol, project.Solution, ct))
        {
            if (includeDeclaration)
                locations.AddRange(referenced.Definition.Locations.Where(l => l.IsInSource));
            locations.AddRange(referenced.Locations.Select(r => r.Location));
        }

        var results = new List<LspLocation>(await HandlerHelpers.ToLocationsAsync(locations, project, ct));

        var contributed = new List<LspLocation>();
        var answered = new List<ILanguageReferenceContributor>();

        foreach (var contributor in LanguageScope.Of(languages).Contributors<ILanguageReferenceContributor>())
        {
            int before = contributed.Count;
            contributed.AddRange(await contributor.ReferencesAsync(symbol, project, ct));

            if (contributed.Count > before)
                answered.Add(contributor);
        }

        // The same withdrawal DefinitionAsync makes, and for the same reason: a generated file
        // mentions the symbol protoc built on every line that marshals it, so leaving those in
        // answers "where is this used" with the machinery rather than with the call sites. Only a
        // pack that put something into this answer may take something out of it.
        if (answered.Count > 0)
            results.RemoveAll(location => answered.Any(pack => pack.Supersedes(location)));

        results.AddRange(contributed);
        return results.Distinct().ToArray();
    }

    public static async Task<LspLocation[]> ImplementationAsync(
        TextDocumentPositionParams p, CancellationToken ct, LanguageSession? languages = null)
    {
        if (await HandlerHelpers.ResolveAsync(p.TextDocument, p.Position, ct) is not
            var (document, _, offset) || document is null)
            return Array.Empty<LspLocation>();

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, offset, ct);
        if (symbol is null)
            return Array.Empty<LspLocation>();

        // The same redirect as DefinitionAsync, and not optional. Roslyn's answer for a dispatcher
        // call falls through every arm below to the `results.Add(symbol)` at the end, which lands
        // Ctrl+F12 on the caret it started from; and a notification, whose handlers are genuinely
        // several, is exactly the question this verb is for. Two navigation verbs disagreeing
        // about one caret is worse than either answer alone.
        var redirected = await RedirectedSymbolsAsync(
            document, offset, symbol, NavigationKind.Implementation, languages, ct);

        if (redirected.Count > 0)
        {
            var redirectedLocations = new List<LspLocation>();
            foreach (var target in redirected)
            {
                redirectedLocations.AddRange(
                    await DefinitionLocationsAsync(target, document.Project, typeDefinition: false, ct));
            }

            return redirectedLocations.Distinct().ToArray();
        }

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

        return await HandlerHelpers.ToLocationsAsync(
            results.SelectMany(s => s.Locations).Where(l => l.IsInSource), document.Project, ct);
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

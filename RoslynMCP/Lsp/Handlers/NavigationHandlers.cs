using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMCP.Languages;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services.ExternalSource;
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

        return await WithContributionsAsync(subjects, document.Project, locations, languages, ct);
    }

    /// <summary>
    /// Adds what the packs say a symbol is declared by, and removes the Roslyn locations they
    /// supersede.
    /// </summary>
    /// <remarks>
    /// Separate from the caller so that a request starting in markup gets the same answer as one
    /// starting in C#. A caret in a <c>&lt;% %&gt;</c> block binds through the projection to the
    /// same code-behind field the C# side binds to, and without this pass it lands on the generated
    /// designer line — the very file the pack exists to redirect away from. Two halves of one
    /// relationship disagreeing about where a control is declared is worse than either answer.
    /// </remarks>
    public static async Task<LspLocation[]> WithContributionsAsync(
        IReadOnlyList<ISymbol> subjects, Project project, List<LspLocation> locations,
        LanguageSession? languages, CancellationToken ct)
    {
        var contributed = new List<LspLocation>();
        var answered = new List<ILanguageDefinitionContributor>();

        foreach (var contributor in
                 LanguageScope.Of(languages).Contributors<ILanguageDefinitionContributor>())
        {
            int before = contributed.Count;

            foreach (var subject in subjects)
                contributed.AddRange(await contributor.DefinitionsAsync(subject, project, ct));

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

        // Metadata symbol (framework/package type). Real source where it can be had, a
        // decompilation where it cannot — the facade decides, and always answers.
        var external = await ExternalSourceService.TryResolveAsync(symbol, project, ct);
        if (external is null)
            return [];

        // Every position, not just the first: a partial framework type is declared in several
        // files, and offering them all is what lets the editor peek rather than guess.
        var uri = LspConverters.PathToUri(external.FilePath);
        return
        [
            .. external.Positions.Select(position => new LspLocation(
                uri,
                new Protocol.Range(
                    new Position(position.Line, position.Character),
                    new Position(position.Line, position.Character)))),
        ];
    }

    /// <summary>
    /// <see cref="DefinitionLocationsAsync"/> followed by the contributor pass — the complete
    /// definition answer for a symbol that did not come from a C# caret.
    /// </summary>
    /// <remarks>
    /// The markup handlers resolve symbols of their own — an <c>Eval</c> argument, a control
    /// attribute — and used to stop at the raw locations, which for a generated member is the
    /// designer file the packs exist to redirect away from. The subject mirrors
    /// <see cref="DefinitionAsync"/>: for typeDefinition the contributors are asked about the
    /// type rather than the member holding it, so the gesture on a generated member still reaches
    /// the model declaring its type.
    /// </remarks>
    public static async Task<LspLocation[]> ContributedDefinitionLocationsAsync(
        ISymbol symbol, Project project, bool typeDefinition, LanguageSession? languages,
        CancellationToken ct)
    {
        var locations = await DefinitionLocationsAsync(symbol, project, typeDefinition, ct);
        var subject = (typeDefinition ? TypeOf(symbol) : symbol).OriginalDefinition;
        return await WithContributionsAsync([subject], project, [.. locations], languages, ct);
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

        // The one caller that may wait: a user pressed Shift+F12 and is looking at a progress
        // indicator, so a pack whose complete answer needs projects the workspace has not loaded
        // may go and load them.
        return await AllReferencesAsync(
            symbol, document.Project, p.Context.IncludeDeclaration, ct, languages,
            waitForCompleteScope: true);
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
    /// <param name="waitForCompleteScope">
    /// Whether a pack may block to widen the search — see
    /// <see cref="ILanguageReferenceContributor.ReferencesAsync"/>. False by default because the
    /// other caller of this method is a code lens, which runs on every scroll.
    /// </param>
    public static async Task<LspLocation[]> AllReferencesAsync(
        ISymbol symbol, Project project, bool includeDeclaration, CancellationToken ct,
        LanguageSession? languages = null, bool waitForCompleteScope = false)
    {
        // The references to a symbol live in the projects that consume its project — the direction
        // lazy loading does not follow — so the caller that may wait, waits for that scope to
        // exist before searching it. The incidental callers search what is open, unchanged.
        var solution = waitForCompleteScope
            ? await Services.SearchScopeService.WidenForSymbolAsync(
                symbol, project, Services.SearchScopeService.ExplicitSearchBudget, ct)
            : project.Solution;

        var locations = new List<Microsoft.CodeAnalysis.Location>();

        // The options Visual Studio's Find All References runs under. The default forwarded by the
        // public overload cascades the inheritance hierarchy in both directions, so a search
        // started on one interface implementation also searches every sibling implementation —
        // members that can never reach the one that was asked about.
        foreach (var referenced in await SymbolFinder.FindReferencesAsync(
                     symbol, solution,
                     FindReferencesSearchOptions.GetFeatureOptionsForStartingSymbol(symbol), ct))
        {
            if (includeDeclaration)
                locations.AddRange(referenced.Definition.Locations.Where(l => l.IsInSource));
            locations.AddRange(referenced.Locations.Select(r => r.Location));
        }

        return await MergeContributedAsync(locations, symbol, project, ct, languages, waitForCompleteScope);
    }

    /// <summary>
    /// The reference count behind a code lens, and the locations its click peeks at.
    /// </summary>
    /// <remarks>
    /// A lens renders a number and peeks at most <paramref name="cap"/> locations, and every
    /// visible lens re-resolves on every scroll and every edit, so this is deliberately not
    /// <see cref="AllReferencesAsync"/>: the search runs implicitly (serial scheduler,
    /// unidirectional cascade) and stops as soon as the cap is exceeded. Contributors are asked
    /// unchanged — a pack's count is the whole reason a mediator handler's lens is not "0".
    /// </remarks>
    /// <returns>
    /// The locations found, and whether the search was stopped by the cap — in which case the
    /// count is a lower bound and must be rendered as such.
    /// </returns>
    public static async Task<(LspLocation[] Locations, bool Capped)> CountedReferencesAsync(
        ISymbol symbol, Project project, int cap, CancellationToken ct,
        LanguageSession? languages = null)
    {
        var locations = new List<Microsoft.CodeAnalysis.Location>();
        bool capped = false;

        using (var collector = new CappedReferenceCollector(cap, ct))
        {
            try
            {
                await SymbolFinder.FindReferencesAsync(
                    symbol, project.Solution, collector, documents: null,
                    s_countingSearch, collector.CancellationToken);
            }
            catch (OperationCanceledException) when (collector.CapReached && !ct.IsCancellationRequested)
            {
                capped = true;
            }

            locations.AddRange(collector.Locations);
        }

        var results = await MergeContributedAsync(
            locations, symbol, project, ct, languages, waitForCompleteScope: false);

        return (results, capped);
    }

    /// <summary>
    /// Implicit, unidirectional: a count in the gutter is nobody's gesture, so it must not run in
    /// parallel against the typing loop, and it wants the references that could actually reach
    /// this member rather than everything related to it.
    /// </summary>
    private static readonly FindReferencesSearchOptions s_countingSearch =
        FindReferencesSearchOptions.Default with
        {
            Explicit = false,
            UnidirectionalHierarchyCascade = true,
        };

    private static async Task<LspLocation[]> MergeContributedAsync(
        List<Microsoft.CodeAnalysis.Location> locations, ISymbol symbol, Project project,
        CancellationToken ct, LanguageSession? languages, bool waitForCompleteScope)
    {
        var results = new List<LspLocation>(await HandlerHelpers.ToLocationsAsync(locations, project, ct));

        var contributed = new List<LspLocation>();
        var answered = new List<ILanguageReferenceContributor>();

        foreach (var contributor in LanguageScope.Of(languages).Contributors<ILanguageReferenceContributor>())
        {
            int before = contributed.Count;
            contributed.AddRange(
                await contributor.ReferencesAsync(symbol, project, ct, waitForCompleteScope));

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

            // The same pass DefinitionAsync runs after its redirect: a handler some pack
            // generated still reaches the line it was generated from.
            return await WithContributionsAsync(
                redirected, document.Project, redirectedLocations, languages, ct);
        }

        return await ImplementationLocationsAsync(symbol, document.Project, languages, ct);
    }

    /// <summary>
    /// The implementations of a symbol, in C# and in the packs' files. Shared with the markup
    /// handlers for the reason <see cref="AllReferencesAsync"/> is: Ctrl+F12 must not answer
    /// differently for the same member depending on which file the gesture started in.
    /// </summary>
    public static async Task<LspLocation[]> ImplementationLocationsAsync(
        ISymbol symbol, Project project, LanguageSession? languages, CancellationToken ct)
    {
        // Implementations of a symbol live in projects that reference its declaring project, which
        // is the one direction lazy loading never took. Ctrl+F12 is always a deliberate gesture,
        // so it may wait for that scope the same way Shift+F12 does.
        var solution = await Services.SearchScopeService.WidenForSymbolAsync(
            symbol, project, Services.SearchScopeService.ExplicitSearchBudget, ct);
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

        var contributed = new List<LspLocation>();
        var answered = new List<ILanguageImplementationContributor>();

        foreach (var contributor in
                 LanguageScope.Of(languages).Contributors<ILanguageImplementationContributor>())
        {
            int before = contributed.Count;
            contributed.AddRange(await contributor.ImplementationsAsync(symbol, project, ct));

            if (contributed.Count > before)
                answered.Add(contributor);
        }

        // Only when nobody could answer. The fallback exists so Ctrl+F12 on a concrete member goes
        // somewhere rather than nowhere, but landing on the caret it was pressed on is the weakest
        // answer there is. The fallback answer is the symbol's own declaration, which for a
        // generated member is a designer line — so the definition pass runs over it, and only a
        // pack that withdrew the declaration may replace it: F12 on a generated property already
        // answers the model line, and this verb falling back must not answer the designer instead.
        // A pack that merely adds a model line beside a hand-written member changes nothing here,
        // or Ctrl+F12 on an override would offer the contract next to the implementation it is on.
        if (results.Count == 0 && contributed.Count == 0)
        {
            var raw = await HandlerHelpers.ToLocationsAsync(
                symbol.OriginalDefinition.Locations.Where(l => l.IsInSource), project, ct);
            var merged = await WithContributionsAsync(
                [symbol.OriginalDefinition], project, [.. raw], languages, ct);

            return raw.Any(location => !merged.Contains(location)) ? merged : raw;
        }

        var locations = new List<LspLocation>(await HandlerHelpers.ToLocationsAsync(
            results.SelectMany(s => s.Locations).Where(l => l.IsInSource), project, ct));

        if (answered.Count > 0)
            locations.RemoveAll(location => answered.Any(pack => pack.Supersedes(location)));

        locations.AddRange(contributed);
        return locations.Distinct().ToArray();
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

        // Same-file scope only, and not by handing the solution-wide engine a document filter:
        // that filter narrows which documents are read, not which symbols are cascaded to, so one
        // caret move can still force cross-project compilations. This is the search Roslyn's own
        // highlighter runs — the dedicated in-documents entry point, and Explicit=false to put it
        // on the exclusive serial scheduler so it never competes with typing. The entry point
        // asserts UnidirectionalHierarchyCascade, which the feature options already set.
        var collector = new StreamingProgressCollector();
        await SymbolFinder.FindReferencesInDocumentsInCurrentProcessAsync(
            symbol, document.Project.Solution, collector, ImmutableHashSet.Create(document),
            FindReferencesSearchOptions.GetFeatureOptionsForStartingSymbol(symbol) with
            {
                Explicit = false,
            },
            ct);

        var tree = await document.GetSyntaxTreeAsync(ct);
        var highlights = new List<DocumentHighlight>();
        foreach (var referenced in collector.GetReferencedSymbols())
        {
            foreach (var loc in referenced.Definition.Locations)
            {
                if (loc.IsInSource && loc.SourceTree == tree)
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

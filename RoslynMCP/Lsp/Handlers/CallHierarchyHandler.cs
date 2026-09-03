using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMCP.Languages;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services.ExternalSource;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>textDocument/prepareCallHierarchy + callHierarchy/incomingCalls/outgoingCalls.
/// Incoming uses <see cref="SymbolFinder.FindCallersAsync(ISymbol, Solution, CancellationToken)"/>;
/// outgoing walks invocations in the symbol's declaration bodies.</summary>
/// <remarks>
/// Each request comes in two halves: one that resolves the position or the item against the
/// workspace, and one that works from a symbol a caller has already resolved. Language packs use
/// the second — they resolve their own positions through their projection — and pass the mapper
/// that turns results in that projection back into their own files.
/// </remarks>
internal static class CallHierarchyHandler
{
    public static async Task<HierarchyItem[]> PrepareAsync(
        TextDocumentPositionParams p, CancellationToken ct)
    {
        var resolved = await HandlerHelpers.ResolveAsync(p.TextDocument, p.Position, ct);
        if (resolved is not var (document, _, offset))
            return Array.Empty<HierarchyItem>();

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, offset, ct);
        return Prepare(symbol, mapper: null);
    }

    /// <summary>The root item for an already-resolved symbol, empty for a symbol a call hierarchy
    /// cannot be rooted at.</summary>
    public static HierarchyItem[] Prepare(ISymbol? symbol, IHierarchySourceMapper? mapper)
    {
        if (symbol is not (IMethodSymbol or IPropertySymbol or IEventSymbol or IFieldSymbol))
            return Array.Empty<HierarchyItem>();

        var item = HierarchyItemFactory.ToItem(symbol, mapper);
        return item is null ? Array.Empty<HierarchyItem>() : [item];
    }

    public static async Task<CallHierarchyIncomingCall[]> IncomingCallsAsync(
        CallHierarchyCallsParams p, CancellationToken ct, LanguageSession? languages = null)
    {
        var (symbol, document) = await HierarchyItemFactory.ResolveSymbolAsync(p.Item, ct);
        if (symbol is null || document is null)
            return Array.Empty<CallHierarchyIncomingCall>();

        // A root in decompiled source: the callers being asked about are the solution's, and the
        // project behind such a file holds nothing but that file. The same mapping find-references
        // makes, so the two verbs answer about one symbol.
        var mapped = await ExternalSymbolBridge.TryMapAsync(
            symbol, document, Services.WorkspaceService.TryGetSessionSolution(), ct);

        var searched = mapped?.Symbol ?? symbol;
        var project = mapped?.Project ?? document.Project;

        var calls = await IncomingCallsAsync(searched, project.Solution, mapper: null, ct);

        // The same seam find-references has, for the same reason: a call written in markup is in
        // no Roslyn document, so FindCallersAsync cannot see it and a code-behind method called
        // only from a page would come back with no callers at all. On a solution with no markup
        // each contributor declines after one metadata lookup.
        var contributed = new List<CallHierarchyIncomingCall>();
        foreach (var contributor in
            LanguageScope.Of(languages).Contributors<ILanguageCallHierarchyContributor>())
        {
            contributed.AddRange(await contributor.IncomingCallsAsync(searched, project, ct));
        }

        return contributed.Count == 0 ? calls : [.. calls, .. contributed];
    }

    public static async Task<CallHierarchyIncomingCall[]> IncomingCallsAsync(
        ISymbol symbol, Solution solution, IHierarchySourceMapper? mapper, CancellationToken ct)
    {
        var callers = await SymbolFinder.FindCallersAsync(symbol, solution, ct);
        var calls = new List<CallHierarchyIncomingCall>();

        foreach (var caller in callers.Where(c => c.IsDirect))
        {
            var sites = caller.Locations
                .Where(l => l.IsInSource)
                .Select(l => HierarchyItemFactory.ToSource(l, mapper))
                .Where(s => s is not null)
                .Select(s => s!.Value)
                .ToList();
            if (sites.Count == 0)
                continue;

            // A caller a projection invented — the method a code block was lifted into — has no
            // declaration anyone can open, so the type it was lifted into stands in for it and is
            // shown at the call itself.
            var from = HierarchyItemFactory.ToItem(caller.CallingSymbol, mapper)
                ?? (mapper is null ? null : Substitute(caller.CallingSymbol, sites[0]));
            if (from is null)
                continue;

            // fromRanges are read against the caller's own file, so a partial type declared
            // elsewhere would produce ranges pointing at the wrong one.
            var fromRanges = sites
                .Where(s => string.Equals(s.Uri, from.Uri, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Range)
                .ToArray();
            if (fromRanges.Length == 0)
                continue;

            calls.Add(new CallHierarchyIncomingCall(from, fromRanges));
        }
        return calls.ToArray();
    }

    public static async Task<CallHierarchyOutgoingCall[]> OutgoingCallsAsync(
        CallHierarchyCallsParams p, CancellationToken ct)
    {
        var (symbol, document) = await HierarchyItemFactory.ResolveSymbolAsync(p.Item, ct);
        if (symbol is null || document is null)
            return Array.Empty<CallHierarchyOutgoingCall>();

        // The declarations walked below are the decompiled file's own — that is the only body
        // there is — but what they call is metadata there and may be source here, which is the
        // half of the answer worth showing.
        var bridge = await ExternalSymbolBridge.TryOpenAsync(
            document, symbol, Services.WorkspaceService.TryGetSessionSolution(), ct);

        return await OutgoingCallsAsync(
            symbol, document.Project.Solution, p.Item.Uri, mapper: null, ct, bridge);
    }

    public static async Task<CallHierarchyOutgoingCall[]> OutgoingCallsAsync(
        ISymbol symbol, Solution solution, string itemUri, IHierarchySourceMapper? mapper,
        CancellationToken ct, ExternalSymbolScope? bridge = null)
    {
        var byTarget = new Dictionary<ISymbol, List<Protocol.Range>>(SymbolEqualityComparer.Default);
        string itemPath = LspConverters.UriToPath(itemUri);

        foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
        {
            // fromRanges are interpreted relative to the item's document — a partial type's
            // other files would produce ranges pointing at the wrong file.
            if (DeclarationPath(syntaxRef, mapper) is not { } declarationPath
                || !string.Equals(declarationPath, itemPath, StringComparison.OrdinalIgnoreCase))
                continue;

            var declaration = await syntaxRef.GetSyntaxAsync(ct);
            var declDocument = solution.GetDocument(syntaxRef.SyntaxTree);
            var model = declDocument is null ? null : await declDocument.GetSemanticModelAsync(ct);
            if (model is null)
                continue;

            foreach (var node in declaration.DescendantNodes())
            {
                if (node is not (InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax))
                    continue;

                if (model.GetSymbolInfo(node, ct).Symbol is not IMethodSymbol target)
                    continue;

                // A target with no declaration is nowhere to navigate to. In decompiled source
                // that is every call, since everything it names is metadata to the one-file
                // project — so the solution is asked whether it declares the target itself, and
                // the call is dropped as before only when it does not.
                ISymbol called = target;
                if (!target.Locations.Any(l => l.IsInSource))
                {
                    if (bridge?.Map(target) is { } inSolution
                        && inSolution.Locations.Any(l => l.IsInSource))
                    {
                        called = inSolution;
                    }
                    else
                    {
                        continue;
                    }
                }

                if (HierarchyItemFactory.ToSource(node.GetLocation(), mapper) is not { } site)
                    continue;

                // Attribute the call to the user-facing symbol (property for accessors, etc.).
                ISymbol display = (called as IMethodSymbol)?.AssociatedSymbol ?? called;
                if (!byTarget.TryGetValue(display, out var ranges))
                    byTarget[display] = ranges = new List<Protocol.Range>();
                ranges.Add(site.Range);
            }
        }

        return byTarget
            .Select(kv => (Item: HierarchyItemFactory.ToItem(kv.Key, mapper), Ranges: kv.Value))
            .Where(x => x.Item is not null)
            .Select(x => new CallHierarchyOutgoingCall(x.Item!, x.Ranges.ToArray()))
            .ToArray();
    }

    /// <summary>The file a declaration is really in, or <c>null</c> when it is generated text
    /// that maps to no file at all.</summary>
    private static string? DeclarationPath(SyntaxReference syntaxRef, IHierarchySourceMapper? mapper)
    {
        string path = syntaxRef.SyntaxTree.FilePath;

        if (mapper?.IsGenerated(path) != true)
            return Services.PathHelper.NormalizePath(path);

        return mapper.ToSource(path, syntaxRef.Span) is { } mapped
            ? LspConverters.UriToPath(mapped.Uri)
            : null;
    }

    private static HierarchyItem? Substitute(
        ISymbol callingSymbol, (string Uri, Protocol.Range Range) site) =>
        callingSymbol.ContainingType is { } owner
            ? HierarchyItemFactory.At(owner, site.Uri, site.Range, site.Range)
            : null;
}

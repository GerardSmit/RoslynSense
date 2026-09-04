using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Services.ExternalSource;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>
/// roslynSense/inheritanceMarkers (custom): per-line inheritance gutter data, Rider-style.
/// Up markers (base / implements / overrides) come from the semantic model — cheap.
/// Down markers (derived / implemented / overridden) need workspace-wide SymbolFinder
/// queries, so they are capped per document and per member.
/// Metadata targets (framework interfaces, package base classes) are listed with a null Uri;
/// roslynSense/resolveInheritanceTarget decompiles them on demand — decompiling eagerly for
/// every marker would be far too slow.
/// </summary>
internal static class InheritanceMarkersHandler
{
    /// <summary>How many targets one marker carries. Shared with the lens, so a count in the
    /// gutter cannot promise more than the list behind the click has.</summary>
    internal const int MaxTargets = 10;

    /// <summary>
    /// How many members in a file get a downward query at all.
    /// </summary>
    /// <remarks>
    /// Each one is a workspace-wide search. The lens list stops at the same number and in the same
    /// order, so there is no member that shows a count here and finds nothing to open.
    /// </remarks>
    internal const int MaxDownQueries = 50;

    public static async Task<InheritanceMarker[]> MarkersAsync(
        InheritanceMarkersParams p, CancellationToken ct)
    {
        var document = await LspDocumentResolver.ResolveAsync(
            LspConverters.UriToPath(p.TextDocument.Uri), ct);
        if (document is null)
            return Array.Empty<InheritanceMarker>();

        // Memoized against the same key codeLens/resolve versions its counts by. The down markers
        // below run the same workspace-wide search a "3 implementations" lens runs, and arriving as
        // a gutter arrow rather than as a lens was the only difference between paying for it once
        // and paying for it on every editor switch, every 700 ms typing pause, and every click on
        // a marker — the client re-requests the whole array to read one line of it.
        var generation = await DocumentSemanticGeneration.ForAsync(document, ct);
        return await InheritanceMarkerMemo.GetAsync(
            p.TextDocument.Uri, generation, () => ComputeAsync(document, CancellationToken.None), ct);
    }

    private static async Task<InheritanceMarker[]> ComputeAsync(Document document, CancellationToken ct)
    {
        // Frozen, as Roslyn's inheritance margin binds: markers re-compute on every typing pause,
        // and the real solution would pay a full rebind plus a generator run each time.
        document = await document.FreezeAsync(ct);

        var root = await document.GetSyntaxRootAsync(ct);
        var text = await document.GetTextAsync(ct);
        var model = await document.GetSemanticModelAsync(ct);
        if (root is null || model is null)
            return Array.Empty<InheritanceMarker>();

        var solution = document.Project.Solution;
        var markers = new List<InheritanceMarker>();
        int downQueries = 0;

        // Decompiled source relates to the solution's types, not to the one file it was opened
        // in — so every marker here is read off the solution's own symbol where it has one. Warm
        // projects only: markers recompute on every typing pause, and the file this runs over is
        // one nobody is typing in.
        var anchor = EnumerateDeclarations(root)
            .Select(d => model.GetDeclaredSymbol(d.Declaration, ct))
            .FirstOrDefault(declared => declared is not null);

        var bridge = await ExternalSymbolBridge.TryOpenAsync(
            document, anchor, WorkspaceService.TryGetSessionSolution(), ct,
            warmProjectsOnly: true);

        foreach (var (declaration, identifier) in EnumerateDeclarations(root))
        {
            ct.ThrowIfCancellationRequested();
            if (model.GetDeclaredSymbol(declaration, ct) is not { } declared)
                continue;

            var symbol = bridge?.Map(declared) ?? declared;
            var searchScope = bridge?.Solution ?? solution;
            var position = text.Lines.GetLinePosition(identifier.Start);

            // The budget is spent by every overridable member, found or not: the lens list counts
            // it the same way, and the two have to run out on the same member.
            bool queryDown = ApplicableDownKind(symbol) is not null && downQueries++ < MaxDownQueries;
            await AppendMarkersAsync(markers, symbol, position, searchScope, queryDown, ct);
        }
        return markers.ToArray();
    }

    /// <summary>
    /// roslynSense/inheritanceAt: the markers for the one declaration around a position — the
    /// member whose identifier a lens sits above, or the one the cursor is somewhere inside.
    /// </summary>
    /// <remarks>
    /// The click behind a lens used to re-request the whole file's markers and pick the entry on
    /// its own line, reporting the lens "out of date" when there was none. That was true after an
    /// edit and false everywhere else it fired: the file-wide pass budgets its downward searches
    /// over every overridable member, while the lens list budgeted only the members it queried,
    /// so past fifty overridable members a lens showed a count the file-wide pass had never
    /// computed. Asked by position there is no array to fall out of step with and no budget to
    /// run out of: it is one member, on a click.
    /// </remarks>
    public static async Task<InheritanceMarker[]> MarkersAtAsync(
        InheritanceAtParams p, CancellationToken ct)
    {
        var resolved = await HandlerHelpers.ResolveAsync(
            p.TextDocument, new Position(p.Line, p.Character), ct);
        if (resolved is not var (document, text, offset))
            return [];

        var root = await document.GetSyntaxRootAsync(ct);
        var model = await document.GetSemanticModelAsync(ct);
        if (root is null || model is null)
            return [];

        // The innermost declaration around the position. A lens's position is the identifier
        // itself; a cursor can be anywhere in the body; either way the nearest enclosing member is
        // the one the question is about.
        var enclosing = root.FindToken(offset).Parent?.AncestorsAndSelf()
            .Select(DeclarationOf)
            .FirstOrDefault(d => d is not null);
        if (enclosing is not var (declaration, identifier)
            || model.GetDeclaredSymbol(declaration, ct) is not { } declared)
            return [];

        // Mapped the way the file-wide pass maps, warm-only included: resolveInheritanceTarget
        // recomputes this list to take one entry out of it by index.
        var mapped = await ExternalSymbolBridge.TryMapAsync(
            declared, document, WorkspaceService.TryGetSessionSolution(), ct, warmProjectsOnly: true);

        var markers = new List<InheritanceMarker>();
        await AppendMarkersAsync(
            markers,
            mapped?.Symbol ?? declared,
            text.Lines.GetLinePosition(identifier.Start),
            mapped?.Project.Solution ?? document.Project.Solution,
            queryDown: true,
            ct);
        return markers.ToArray();
    }

    /// <summary>
    /// The markers for one declaration: every up relation, and the down relation when
    /// <paramref name="queryDown"/> allows the workspace-wide search it costs.
    /// </summary>
    private static async Task AppendMarkersAsync(
        List<InheritanceMarker> markers, ISymbol symbol, LinePosition position,
        Solution searchScope, bool queryDown, CancellationToken ct)
    {
        foreach (string kind in ApplicableUpKinds(symbol))
        {
            var targets = ComputeUpTargets(symbol, kind)
                .Select(t => ToTarget(t.Symbol, t.Title))
                .ToArray();
            if (targets.Length > 0)
                markers.Add(new InheritanceMarker(position.Line, position.Character, kind, targets));
        }

        if (queryDown && ApplicableDownKind(symbol) is { } downKind)
        {
            var targets = (await ComputeDownTargetsAsync(symbol, downKind, searchScope, ct))
                .Select(t => ToTarget(t.Symbol, t.Title))
                .Where(t => t.Uri is not null) // derived/implementing types are source symbols
                .Take(MaxTargets)
                .ToArray();
            if (targets.Length > 0)
                markers.Add(new InheritanceMarker(position.Line, position.Character, downKind, targets));
        }
    }

    /// <summary>roslynSense/resolveInheritanceTarget: re-resolves one marker target and, for
    /// metadata symbols, decompiles the containing type to give the editor a real location.</summary>
    public static async Task<LspLocation?> ResolveTargetAsync(
        ResolveInheritanceTargetParams p, CancellationToken ct)
    {
        var resolved = await HandlerHelpers.ResolveAsync(
            p.TextDocument, new Position(p.Line, p.Character), ct);
        if (resolved is not var (document, _, offset))
            return null;

        var found = await SymbolFinder.FindSymbolAtPositionAsync(document, offset, ct);
        if (found is null)
            return null;

        // Mapped exactly as the markers were, warm-only included: this re-resolves the target list
        // to take one entry out of it by index, so a list computed differently would open the
        // wrong thing.
        var mapped = await ExternalSymbolBridge.TryMapAsync(
            found, document, WorkspaceService.TryGetSessionSolution(), ct, warmProjectsOnly: true);

        var symbol = mapped?.Symbol ?? found;

        var targets = ComputeUpTargets(symbol, p.Kind);
        if (targets.Count == 0)
        {
            targets = await ComputeDownTargetsAsync(
                symbol, p.Kind, mapped?.Project.Solution ?? document.Project.Solution, ct);
        }
        if (p.Index < 0 || p.Index >= targets.Count)
            return null;

        var target = targets[p.Index].Symbol;
        var sourceLocation = target.Locations.FirstOrDefault(l => l.IsInSource);
        if (sourceLocation is not null)
            return LspConverters.ToLocation(sourceLocation);

        var external = await ExternalSourceService.TryResolveAsync(target, document.Project, ct);
        if (external is null)
            return null;

        return new LspLocation(
            LspConverters.PathToUri(external.FilePath),
            new Protocol.Range(
                new Position(external.Primary.Line, external.Primary.Character),
                new Position(external.Primary.Line, external.Primary.Character)));
    }

    internal static IEnumerable<string> ApplicableUpKinds(ISymbol symbol)
    {
        switch (symbol)
        {
            case INamedTypeSymbol:
                yield return "base";
                break;
            case IMethodSymbol or IPropertySymbol or IEventSymbol when symbol.IsOverride:
                yield return "overrides";
                break;
            case IMethodSymbol or IPropertySymbol or IEventSymbol:
                yield return "implements";
                break;
        }
    }

    internal static string? ApplicableDownKind(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol { TypeKind: TypeKind.Interface } => "derived",
        INamedTypeSymbol { TypeKind: TypeKind.Class, IsSealed: false } => "derived",
        IMethodSymbol or IPropertySymbol or IEventSymbol
            when symbol.ContainingType?.TypeKind == TypeKind.Interface => "implemented",
        IMethodSymbol or IPropertySymbol or IEventSymbol
            when symbol.IsVirtual || symbol.IsAbstract || symbol is { IsOverride: true, IsSealed: false }
            => "overridden",
        _ => null,
    };

    /// <summary>Deterministic order — resolveInheritanceTarget picks by index.</summary>
    internal static List<(ISymbol Symbol, string Title)> ComputeUpTargets(ISymbol symbol, string kind)
    {
        var targets = new List<(ISymbol, string)>();
        switch (kind)
        {
            case "base" when symbol is INamedTypeSymbol type:
                // Implicit bases (Object, Enum, ValueType, Delegate) are noise, not inheritance.
                if (type.BaseType is
                    {
                        SpecialType: not (SpecialType.System_Object or SpecialType.System_Enum
                            or SpecialType.System_ValueType or SpecialType.System_MulticastDelegate
                            or SpecialType.System_Delegate)
                    } baseType)
                    targets.Add((baseType, $"base: {baseType.Name}"));
                foreach (var iface in type.Interfaces)
                    targets.Add((iface, $"implements {iface.Name}"));
                break;

            case "overrides" when Overridden(symbol) is { } overridden:
                targets.Add((overridden,
                    $"overrides {overridden.ContainingType?.Name}.{overridden.Name}"));
                break;

            case "implements" when FindImplementedInterfaceMember(symbol) is { } ifaceMember:
                targets.Add((ifaceMember,
                    $"implements {ifaceMember.ContainingType?.Name}.{ifaceMember.Name}"));
                break;
        }
        return targets;
    }

    internal static async Task<List<(ISymbol Symbol, string Title)>> ComputeDownTargetsAsync(
        ISymbol symbol, string kind, Solution solution, CancellationToken ct)
    {
        var targets = new List<(ISymbol, string)>();
        switch (kind)
        {
            case "derived" when symbol is INamedTypeSymbol { TypeKind: TypeKind.Interface } iface:
                foreach (var d in await SymbolFinder.FindDerivedInterfacesAsync(
                    iface, solution, transitive: false, cancellationToken: ct))
                    targets.Add((d, $"derived: {d.Name}"));
                foreach (var d in await SymbolFinder.FindImplementationsAsync(
                    iface, solution, transitive: false, cancellationToken: ct))
                    targets.Add((d, $"implemented by {d.Name}"));
                break;

            case "derived" when symbol is INamedTypeSymbol type:
                foreach (var d in await SymbolFinder.FindDerivedClassesAsync(
                    type, solution, transitive: false, cancellationToken: ct))
                    targets.Add((d, $"derived: {d.Name}"));
                break;

            case "implemented":
                foreach (var impl in await SymbolFinder.FindImplementationsAsync(
                    symbol, solution, cancellationToken: ct))
                    targets.Add((impl, $"implemented by {impl.ContainingType?.Name}.{impl.Name}"));
                break;

            case "overridden":
                foreach (var o in await SymbolFinder.FindOverridesAsync(
                    symbol, solution, cancellationToken: ct))
                    targets.Add((o, $"overridden by {o.ContainingType?.Name}.{o.Name}"));
                break;
        }
        return targets;
    }

    private static InheritanceTarget ToTarget(ISymbol symbol, string title)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (location is not null && LspConverters.ToLocation(location) is { } lsp)
            return new InheritanceTarget(title, lsp.Uri, lsp.Range.Start.Line, lsp.Range.Start.Character);
        return new InheritanceTarget(title, Uri: null, 0, 0); // metadata — resolved on click
    }

    private static ISymbol? Overridden(ISymbol symbol) => symbol switch
    {
        IMethodSymbol m => m.OverriddenMethod,
        IPropertySymbol p => p.OverriddenProperty,
        IEventSymbol e => e.OverriddenEvent,
        _ => null,
    };

    private static ISymbol? FindImplementedInterfaceMember(ISymbol symbol) =>
        symbol.ContainingType?.AllInterfaces
            .SelectMany(i => i.GetMembers())
            .FirstOrDefault(m => SymbolEqualityComparer.Default.Equals(
                symbol.ContainingType.FindImplementationForInterfaceMember(m), symbol));

    private static IEnumerable<(SyntaxNode Declaration, TextSpan Identifier)> EnumerateDeclarations(SyntaxNode root) =>
        root.DescendantNodes()
            .Select(DeclarationOf)
            .Where(d => d is not null)
            .Select(d => d!.Value);

    /// <summary>The node as a declaration a marker can sit on, with the span the marker anchors
    /// to, or <see langword="null"/> for any other node.</summary>
    private static (SyntaxNode Declaration, TextSpan Identifier)? DeclarationOf(SyntaxNode node) => node switch
    {
        BaseTypeDeclarationSyntax type => (type, type.Identifier.Span),
        MethodDeclarationSyntax method => (method, method.Identifier.Span),
        PropertyDeclarationSyntax property => (property, property.Identifier.Span),
        EventDeclarationSyntax ev => (ev, ev.Identifier.Span),
        IndexerDeclarationSyntax indexer => (indexer, indexer.ThisKeyword.Span),
        _ => null,
    };
}

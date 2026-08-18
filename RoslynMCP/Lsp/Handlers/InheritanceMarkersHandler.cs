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
    private const int MaxTargets = 10;
    private const int MaxDownQueries = 50;

    public static async Task<InheritanceMarker[]> MarkersAsync(
        InheritanceMarkersParams p, CancellationToken ct)
    {
        var document = await LspDocumentResolver.ResolveAsync(
            LspConverters.UriToPath(p.TextDocument.Uri), ct);
        if (document is null)
            return Array.Empty<InheritanceMarker>();

        var root = await document.GetSyntaxRootAsync(ct);
        var text = await document.GetTextAsync(ct);
        var model = await document.GetSemanticModelAsync(ct);
        if (root is null || model is null)
            return Array.Empty<InheritanceMarker>();

        var solution = document.Project.Solution;
        var markers = new List<InheritanceMarker>();
        int downQueries = 0;

        foreach (var (declaration, identifier) in EnumerateDeclarations(root))
        {
            ct.ThrowIfCancellationRequested();
            if (model.GetDeclaredSymbol(declaration, ct) is not { } symbol)
                continue;

            var position = text.Lines.GetLinePosition(identifier.Start);

            foreach (string kind in ApplicableUpKinds(symbol))
            {
                var targets = ComputeUpTargets(symbol, kind)
                    .Select(t => ToTarget(t.Symbol, t.Title))
                    .ToArray();
                if (targets.Length > 0)
                    markers.Add(new InheritanceMarker(position.Line, position.Character, kind, targets));
            }

            if (ApplicableDownKind(symbol) is { } downKind && downQueries++ < MaxDownQueries)
            {
                var targets = (await ComputeDownTargetsAsync(symbol, downKind, solution, ct))
                    .Select(t => ToTarget(t.Symbol, t.Title))
                    .Where(t => t.Uri is not null) // derived/implementing types are source symbols
                    .Take(MaxTargets)
                    .ToArray();
                if (targets.Length > 0)
                    markers.Add(new InheritanceMarker(position.Line, position.Character, downKind, targets));
            }
        }
        return markers.ToArray();
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

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, offset, ct);
        if (symbol is null)
            return null;

        var targets = ComputeUpTargets(symbol, p.Kind);
        if (targets.Count == 0)
        {
            targets = await ComputeDownTargetsAsync(symbol, p.Kind, document.Project.Solution, ct);
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

    private static IEnumerable<(SyntaxNode Declaration, TextSpan Identifier)> EnumerateDeclarations(SyntaxNode root)
    {
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case BaseTypeDeclarationSyntax type:
                    yield return (type, type.Identifier.Span);
                    break;
                case MethodDeclarationSyntax method:
                    yield return (method, method.Identifier.Span);
                    break;
                case PropertyDeclarationSyntax property:
                    yield return (property, property.Identifier.Span);
                    break;
                case EventDeclarationSyntax ev:
                    yield return (ev, ev.Identifier.Span);
                    break;
                case IndexerDeclarationSyntax indexer:
                    yield return (indexer, indexer.ThisKeyword.Span);
                    break;
            }
        }
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>textDocument/prepareTypeHierarchy + typeHierarchy/supertypes/subtypes.
/// Direct relationships only (each level expands on demand); metadata-only types are
/// skipped because hierarchy items need a source location to navigate to.</summary>
/// <remarks>
/// Split the same way as <see cref="CallHierarchyHandler"/>: the parameter overloads resolve
/// against the workspace, and the symbol overloads serve a language pack that resolved the
/// position itself and needs its projection mapped out of the answer.
/// </remarks>
internal static class TypeHierarchyHandler
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

    /// <summary>The root item for an already-resolved symbol, empty for anything that is not a
    /// type.</summary>
    public static HierarchyItem[] Prepare(ISymbol? symbol, IHierarchySourceMapper? mapper)
    {
        if (GetNamedType(symbol) is not { } type)
            return Array.Empty<HierarchyItem>();

        var item = HierarchyItemFactory.ToItem(type, mapper);
        return item is null ? Array.Empty<HierarchyItem>() : [item];
    }

    public static async Task<HierarchyItem[]> SupertypesAsync(
        TypeHierarchyItemParams p, CancellationToken ct)
    {
        var (symbol, _) = await HierarchyItemFactory.ResolveSymbolAsync(p.Item, ct);
        return Supertypes(symbol, mapper: null);
    }

    public static HierarchyItem[] Supertypes(ISymbol? symbol, IHierarchySourceMapper? mapper)
    {
        if (GetNamedType(symbol) is not { } type)
            return Array.Empty<HierarchyItem>();

        var supertypes = new List<INamedTypeSymbol>();
        if (type.BaseType is { SpecialType: not SpecialType.System_Object } baseType)
            supertypes.Add(baseType);
        supertypes.AddRange(type.Interfaces);

        return supertypes
            .Select(t => HierarchyItemFactory.ToItem(t, mapper))
            .Where(i => i is not null)
            .Select(i => i!)
            .ToArray();
    }

    public static async Task<HierarchyItem[]> SubtypesAsync(
        TypeHierarchyItemParams p, CancellationToken ct)
    {
        var (symbol, document) = await HierarchyItemFactory.ResolveSymbolAsync(p.Item, ct);
        if (document is null)
            return Array.Empty<HierarchyItem>();

        return await SubtypesAsync(symbol, document.Project.Solution, mapper: null, ct);
    }

    public static async Task<HierarchyItem[]> SubtypesAsync(
        ISymbol? symbol, Solution solution, IHierarchySourceMapper? mapper, CancellationToken ct)
    {
        if (GetNamedType(symbol) is not { } type)
            return Array.Empty<HierarchyItem>();

        var subtypes = new List<INamedTypeSymbol>();
        if (type.TypeKind == TypeKind.Interface)
        {
            subtypes.AddRange(await SymbolFinder.FindDerivedInterfacesAsync(
                type, solution, transitive: false, cancellationToken: ct));
            subtypes.AddRange(await SymbolFinder.FindImplementationsAsync(
                type, solution, transitive: false, cancellationToken: ct));
        }
        else
        {
            subtypes.AddRange(await SymbolFinder.FindDerivedClassesAsync(
                type, solution, transitive: false, cancellationToken: ct));
        }

        return subtypes
            .Select(t => HierarchyItemFactory.ToItem(t, mapper))
            .Where(i => i is not null)
            .Select(i => i!)
            .Take(200)
            .ToArray();
    }

    private static INamedTypeSymbol? GetNamedType(ISymbol? symbol) => symbol switch
    {
        INamedTypeSymbol named => named,
        IMethodSymbol { MethodKind: MethodKind.Constructor } ctor => ctor.ContainingType,
        _ => null,
    };
}

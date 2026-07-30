using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>textDocument/prepareTypeHierarchy + typeHierarchy/supertypes/subtypes.
/// Direct relationships only (each level expands on demand); metadata-only types are
/// skipped because hierarchy items need a source location to navigate to.</summary>
internal static class TypeHierarchyHandler
{
    public static async Task<HierarchyItem[]> PrepareAsync(
        TextDocumentPositionParams p, CancellationToken ct)
    {
        var resolved = await HandlerHelpers.ResolveAsync(p.TextDocument, p.Position, ct);
        if (resolved is not var (document, _, offset))
            return Array.Empty<HierarchyItem>();

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, offset, ct);
        if (GetNamedType(symbol) is not { } type)
            return Array.Empty<HierarchyItem>();

        var item = HierarchyItemFactory.ToItem(type);
        return item is null ? Array.Empty<HierarchyItem>() : [item];
    }

    public static async Task<HierarchyItem[]> SupertypesAsync(
        TypeHierarchyItemParams p, CancellationToken ct)
    {
        var (symbol, _) = await HierarchyItemFactory.ResolveSymbolAsync(p.Item, ct);
        if (GetNamedType(symbol) is not { } type)
            return Array.Empty<HierarchyItem>();

        var supertypes = new List<INamedTypeSymbol>();
        if (type.BaseType is { SpecialType: not SpecialType.System_Object } baseType)
            supertypes.Add(baseType);
        supertypes.AddRange(type.Interfaces);

        return supertypes
            .Select(HierarchyItemFactory.ToItem)
            .Where(i => i is not null)
            .Select(i => i!)
            .ToArray();
    }

    public static async Task<HierarchyItem[]> SubtypesAsync(
        TypeHierarchyItemParams p, CancellationToken ct)
    {
        var (symbol, document) = await HierarchyItemFactory.ResolveSymbolAsync(p.Item, ct);
        if (GetNamedType(symbol) is not { } type || document is null)
            return Array.Empty<HierarchyItem>();

        var solution = document.Project.Solution;
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
            .Select(HierarchyItemFactory.ToItem)
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

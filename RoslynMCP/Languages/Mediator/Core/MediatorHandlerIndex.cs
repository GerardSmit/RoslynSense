using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace RoslynMCP.Languages.Mediator.Core;

/// <summary>
/// The source types in a compilation that implement any handler interface, computed once per
/// <see cref="Compilation"/>.
/// </summary>
/// <remarks>
/// The scan behind this walks every declared type in the assembly and asks each for its
/// <see cref="ITypeSymbol.AllInterfaces"/> — exactly the cost that must not be paid per
/// navigation. A compilation is immutable, so the answer cannot change under it; a keystroke
/// produces a new compilation whose index is built on first ask, and the old one falls out with
/// its compilation. Handlers are indexed without regard to which message they handle: matching a
/// message is the caller's per-request question, and the handler list is the per-compilation one.
/// </remarks>
internal static class MediatorHandlerIndex
{
    private static readonly ConditionalWeakTable<Compilation, IReadOnlyList<INamedTypeSymbol>> s_handlers = new();

    public static IReadOnlyList<INamedTypeSymbol> HandlerTypes(Compilation compilation, CancellationToken ct)
    {
        if (s_handlers.TryGetValue(compilation, out var cached))
            return cached;

        return s_handlers.GetValue(compilation, c => Build(c, ct));
    }

    private static IReadOnlyList<INamedTypeSymbol> Build(Compilation compilation, CancellationToken ct)
    {
        var found = new List<INamedTypeSymbol>();

        foreach (var type in DeclaredTypes(compilation.Assembly.GlobalNamespace, ct))
        {
            if (MediatorSymbols.HandlerInterfacesOf(type).Length > 0)
                found.Add(type);
        }

        return found;
    }

    private static IEnumerable<INamedTypeSymbol> DeclaredTypes(INamespaceSymbol root, CancellationToken ct)
    {
        foreach (var member in root.GetMembers())
        {
            ct.ThrowIfCancellationRequested();

            switch (member)
            {
                case INamespaceSymbol nested:
                    foreach (var type in DeclaredTypes(nested, ct))
                        yield return type;
                    break;

                case INamedTypeSymbol type:
                    yield return type;
                    foreach (var nested in type.GetTypeMembers())
                        yield return nested;
                    break;
            }
        }
    }
}

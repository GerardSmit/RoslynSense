using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace RoslynMCP.Languages.Mediator.Core;

/// <summary>Which of the two libraries something belongs to.</summary>
/// <remarks>
/// Carried on everything the pack produces because the two share every simple name and arity that
/// matters — <c>IRequestHandler&lt;,&gt;</c>, <c>ISender.Send</c>, <c>INotificationHandler&lt;&gt;</c>
/// — and a project may legitimately reference both. Only the namespace separates them.
/// </remarks>
[Flags]
internal enum MediatorFlavor
{
    None = 0,
    MediatR = 1,
    Zapto = 2,
}

/// <summary>What a message is dispatched as.</summary>
internal enum MediatorMessageKind
{
    Request,
    Notification,
    StreamRequest,
}

/// <summary>
/// The library types a compilation resolves, and the gate in front of everything else the pack
/// does.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="For"/> is the whole cost of this pack in a solution that does not use a mediator: one
/// metadata lookup per compilation, memoized, and every contributor returns an empty list before
/// touching a syntax tree. That mirrors <c>AspxReferenceService.HostsWebFormsAsync</c> and
/// <c>ProtoReferenceService.HostsProtobufAsync</c>, and it is what
/// <c>ILanguagePack.WellKnownTypeNames</c> describes but does not itself enforce for contributors.
/// </para>
/// <para>
/// Memoized on the <see cref="Compilation"/> through a <see cref="ConditionalWeakTable"/>, so a
/// superseded snapshot drops its entry without anything having to invalidate it, and no symbol is
/// held past the compilation that owns it.
/// </para>
/// </remarks>
internal sealed class MediatorTypes
{
    public const string MediatRNamespace = "MediatR";
    public const string ZaptoNamespace = "Zapto.Mediator";

    /// <summary>The class Zapto's generator emits its extension methods into.</summary>
    public const string SenderExtensionsName = "SenderExtensions";

    /// <summary>The class it emits the handler registrations into.</summary>
    public const string AssemblyExtensionsName = "AssemblyExtensions";

    private static readonly ConditionalWeakTable<Compilation, StrongBox<MediatorTypes?>> s_cache = new();

    private static readonly string[] s_handlerInterfaceNames =
    [
        "IRequestHandler`1", "IRequestHandler`2", "INotificationHandler`1", "IStreamRequestHandler`2",
    ];

    /// <summary>
    /// The abstract bases a handler can reach its interface through. They matter because a class
    /// deriving from one is not an interface implementer in Roslyn's eyes — the base implements the
    /// interface member explicitly and exposes a differently shaped <c>Handle</c> for the user to
    /// override — so nothing that looks only at interface implementations finds the method the user
    /// actually wrote.
    /// </summary>
    private static readonly string[] s_handlerBaseNames =
    [
        "RequestHandler`1", "RequestHandler`2", "AsyncRequestHandler`1", "AsyncRequestHandler`2",
        "NotificationHandler`1",
    ];

    private static readonly string[] s_dispatchInterfaceNames =
    [
        "ISender", "IPublisher", "IBackgroundPublisher", "IMediator",
    ];

    private MediatorTypes(
        MediatorFlavor flavor,
        INamedTypeSymbol? unit,
        INamedTypeSymbol? mediatorNamespace,
        ImmutableArray<INamedTypeSymbol> dispatchInterfaces,
        ImmutableArray<INamedTypeSymbol> handlerInterfaces,
        ImmutableArray<INamedTypeSymbol> handlerBases)
    {
        Flavor = flavor;
        Unit = unit;
        MediatorNamespaceType = mediatorNamespace;
        DispatchInterfaces = dispatchInterfaces;
        HandlerInterfaces = handlerInterfaces;
        HandlerBases = handlerBases;
    }

    public MediatorFlavor Flavor { get; }

    /// <summary><c>MediatR.Unit</c>, the stand-in for a request with no response.</summary>
    public INamedTypeSymbol? Unit { get; }

    /// <summary><c>Zapto.Mediator.MediatorNamespace</c>, which every dispatch method has an
    /// overload taking first — which is why no argument is ever located by index.</summary>
    public INamedTypeSymbol? MediatorNamespaceType { get; }

    public ImmutableArray<INamedTypeSymbol> DispatchInterfaces { get; }

    /// <summary>The open generic handler interfaces, of whichever libraries are present.</summary>
    public ImmutableArray<INamedTypeSymbol> HandlerInterfaces { get; }

    public ImmutableArray<INamedTypeSymbol> HandlerBases { get; }

    /// <summary>
    /// The mediator types <paramref name="compilation"/> resolves, or null when it hosts no
    /// mediator at all.
    /// </summary>
    public static MediatorTypes? For(Compilation compilation) =>
        s_cache.GetValue(compilation, static c => new StrongBox<MediatorTypes?>(Resolve(c))).Value;

    private static MediatorTypes? Resolve(Compilation compilation)
    {
        // One probe answers for both libraries: Zapto.Mediator takes its markers from the
        // MediatR.Contracts package rather than declaring its own, so IBaseRequest is present
        // wherever either is.
        if (compilation.GetTypeByMetadataName($"{MediatRNamespace}.IBaseRequest") is null)
            return null;

        var flavor = MediatorFlavor.None;
        if (compilation.GetTypeByMetadataName($"{MediatRNamespace}.ISender") is not null)
            flavor |= MediatorFlavor.MediatR;
        if (compilation.GetTypeByMetadataName($"{ZaptoNamespace}.ISender") is not null)
            flavor |= MediatorFlavor.Zapto;

        // Contracts and nothing else: the project declares messages but has no way to dispatch or
        // handle one, so there is nothing here for the pack to find. The dispatch sites live in
        // projects that do reference a mediator, and those resolve their own compilation.
        if (flavor == MediatorFlavor.None)
            return null;

        return new MediatorTypes(
            flavor,
            compilation.GetTypeByMetadataName($"{MediatRNamespace}.Unit"),
            compilation.GetTypeByMetadataName($"{ZaptoNamespace}.MediatorNamespace"),
            ResolveAll(compilation, s_dispatchInterfaceNames),
            ResolveAll(compilation, s_handlerInterfaceNames),
            ResolveAll(compilation, s_handlerBaseNames));
    }

    /// <summary>
    /// Every name that resolves, under either namespace. Both are tried for every name rather than
    /// gated on <see cref="Flavor"/>: a project can reference one library's dispatcher and the
    /// other's handlers, and a name that does not resolve simply contributes nothing.
    /// </summary>
    private static ImmutableArray<INamedTypeSymbol> ResolveAll(Compilation compilation, string[] names)
    {
        var resolved = ImmutableArray.CreateBuilder<INamedTypeSymbol>();

        foreach (string name in names)
        {
            foreach (string ns in (string[])[MediatRNamespace, ZaptoNamespace])
            {
                if (compilation.GetTypeByMetadataName($"{ns}.{name}") is { } type)
                    resolved.Add(type);
            }
        }

        return resolved.ToImmutable();
    }
}

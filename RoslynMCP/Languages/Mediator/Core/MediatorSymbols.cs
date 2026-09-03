using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace RoslynMCP.Languages.Mediator.Core;

/// <summary>A request, notification or stream request, with what it answers with.</summary>
internal readonly record struct MediatorMessage(
    INamedTypeSymbol Type, MediatorMessageKind Kind, ITypeSymbol? ResponseType);

/// <summary>One handler interface a type implements, and the message it handles.</summary>
internal readonly record struct MediatorHandlerInterface(
    INamedTypeSymbol Interface, MediatorMessage Message, MediatorFlavor Flavor);

/// <summary>A <c>Send</c>, <c>Publish</c> or <c>CreateStream</c>, and where its message is.</summary>
internal readonly record struct MediatorDispatch(
    IMethodSymbol Method,
    MediatorMessageKind Kind,
    MediatorFlavor Flavor,
    ITypeParameterSymbol? MessageTypeParameter,
    IParameterSymbol? MessageParameter);

/// <summary>
/// What the two libraries' shapes look like to Roslyn. Everything here is a predicate over symbols
/// — no searching, no I/O — so both directions of navigation can share one idea of what a handler,
/// a message and a dispatch are.
/// </summary>
internal static class MediatorSymbols
{
    /// <summary>The methods a mediator is asked to dispatch through.</summary>
    private static readonly string[] s_dispatchMethodNames = ["Send", "Publish", "CreateStream"];

    /// <summary>The builder methods a delegate handler is registered with.</summary>
    private static readonly string[] s_delegateRegistrationNames =
        ["AddRequestHandler", "AddNotificationHandler", "AddStreamRequestHandler", "AddDefaultRequestHandler"];

    public static string? NamespaceOf(ISymbol? symbol) =>
        symbol?.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : null;

    public static bool IsMediatorNamespace(string? ns) =>
        ns is MediatorTypes.MediatRNamespace or MediatorTypes.ZaptoNamespace;

    public static MediatorFlavor FlavorOf(ISymbol? symbol) => NamespaceOf(symbol) switch
    {
        MediatorTypes.ZaptoNamespace => MediatorFlavor.Zapto,
        MediatorTypes.MediatRNamespace => MediatorFlavor.MediatR,
        _ => MediatorFlavor.None,
    };

    /// <summary>Whether this is one of the marker interfaces a message is declared with.</summary>
    public static bool IsMessageMarker(ITypeSymbol? type) =>
        type is INamedTypeSymbol named
        && IsMediatorNamespace(NamespaceOf(named))
        && (named.Name, named.Arity) is
            ("IRequest", 0) or ("IRequest", 1) or ("IBaseRequest", 0)
            or ("INotification", 0) or ("IStreamRequest", 1);

    public static bool IsDelegateRegistration(string name) =>
        Array.IndexOf(s_delegateRegistrationNames, name) >= 0;

    /// <summary>
    /// The message <paramref name="type"/> is, if it is one.
    /// </summary>
    /// <remarks>
    /// The marker interfaces themselves are excluded: a <c>Send(IRequest)</c> parameter is a
    /// parameter, not a message, and treating it as one would make every dispatch look like a
    /// dispatch of the marker.
    /// </remarks>
    public static bool TryGetMessage(ITypeSymbol? type, out MediatorMessage message)
    {
        message = default;

        if (type is not INamedTypeSymbol named || IsMessageMarker(named))
            return false;

        foreach (var candidate in named.AllInterfaces)
        {
            if (!IsMediatorNamespace(NamespaceOf(candidate)))
                continue;

            MediatorMessageKind kind;
            ITypeSymbol? response;

            switch (candidate.Name, candidate.Arity)
            {
                case ("IRequest", 0):
                    (kind, response) = (MediatorMessageKind.Request, null);
                    break;
                case ("IRequest", 1):
                    (kind, response) = (MediatorMessageKind.Request, candidate.TypeArguments[0]);
                    break;
                case ("INotification", 0):
                    (kind, response) = (MediatorMessageKind.Notification, null);
                    break;
                case ("IStreamRequest", 1):
                    (kind, response) = (MediatorMessageKind.StreamRequest, candidate.TypeArguments[0]);
                    break;
                default:
                    continue;
            }

            // A type implementing both IRequest and IRequest<T> keeps the one naming a response,
            // which is the overload set the generator keys on too.
            if (message.Type is null || (message.ResponseType is null && response is not null))
                message = new MediatorMessage(named, kind, response);
        }

        return message.Type is not null;
    }

    /// <summary>Every handler interface <paramref name="type"/> implements, however it reaches it.</summary>
    /// <remarks>
    /// <see cref="ITypeSymbol.AllInterfaces"/> rather than the declared list, because that is what
    /// makes a handler reachable only through an abstract base — or through a generic base whose
    /// type argument was substituted several levels up — resolve to the message it really handles.
    /// </remarks>
    public static ImmutableArray<MediatorHandlerInterface> HandlerInterfacesOf(INamedTypeSymbol? type)
    {
        if (type is null)
            return [];

        var found = ImmutableArray.CreateBuilder<MediatorHandlerInterface>();

        foreach (var candidate in type.AllInterfaces)
        {
            var flavor = FlavorOf(candidate);
            if (flavor == MediatorFlavor.None)
                continue;

            bool isHandler = (candidate.Name, candidate.Arity) is
                ("IRequestHandler", 1) or ("IRequestHandler", 2)
                or ("INotificationHandler", 1) or ("IStreamRequestHandler", 2);

            if (!isHandler || !TryGetMessage(candidate.TypeArguments[0], out var message))
                continue;

            found.Add(new MediatorHandlerInterface(candidate, message, flavor));
        }

        return found.ToImmutable();
    }

    /// <summary>
    /// Whether <paramref name="method"/> is the <c>Handle</c> a handler runs.
    /// </summary>
    /// <remarks>
    /// Identified by ownership and never by shape. Zapto's <c>Handle</c> takes a leading
    /// <c>IServiceProvider</c> where MediatR's does not, and the abstract bases declare a third
    /// shape again, so any check counting parameters would need one arm per library and would still
    /// break on the next overload. Ownership is the same question in all three cases.
    /// </remarks>
    public static bool IsHandleMethod(IMethodSymbol method, MediatorTypes types)
    {
        if (!IsNamedHandle(method) || method.ContainingType is not { } owner)
            return false;

        var handlerInterfaces = HandlerInterfacesOf(owner);
        if (handlerInterfaces.Length == 0)
            return false;

        foreach (var explicitly in method.ExplicitInterfaceImplementations)
        {
            if (handlerInterfaces.Any(h =>
                    SymbolEqualityComparer.Default.Equals(h.Interface, explicitly.ContainingType)))
            {
                return true;
            }
        }

        foreach (var handler in handlerInterfaces)
        {
            foreach (var member in handler.Interface.GetMembers("Handle"))
            {
                if (SymbolEqualityComparer.Default.Equals(
                        owner.FindImplementationForInterfaceMember(member), method))
                {
                    return true;
                }
            }
        }

        return OverridesHandlerBase(method, types);
    }

    /// <summary>
    /// Whether the method overrides the <c>Handle</c> one of the library's abstract bases declares.
    /// </summary>
    public static bool OverridesHandlerBase(IMethodSymbol method, MediatorTypes types)
    {
        var root = method;
        while (root.OverriddenMethod is { } overridden)
            root = overridden;

        if (ReferenceEquals(root, method) && !method.IsOverride)
            return false;

        var declaring = root.ContainingType?.OriginalDefinition;
        return declaring is not null
            && types.HandlerBases.Any(b => SymbolEqualityComparer.Default.Equals(b, declaring));
    }

    /// <summary>
    /// The <c>Handle</c> in <paramref name="handler"/> the user actually wrote, for
    /// <paramref name="message"/>.
    /// </summary>
    /// <remarks>
    /// Not the interface's member: for a handler deriving from one of the abstract bases that
    /// resolves to the base's own explicit implementation, in metadata, and navigating there lands
    /// the caret inside the library rather than in the code that runs.
    /// </remarks>
    public static IMethodSymbol? HandleMethodFor(
        INamedTypeSymbol handler, MediatorMessage message, MediatorTypes types)
    {
        IMethodSymbol? fallback = null;

        foreach (var member in handler.GetMembers().OfType<IMethodSymbol>())
        {
            if (!IsNamedHandle(member) || !member.Locations.Any(l => l.IsInSource))
                continue;

            // The message this Handle is for, when the handler has more than one.
            foreach (var handled in member.ExplicitInterfaceImplementations)
            {
                if (handled.ContainingType.TypeArguments.Length > 0
                    && MessagesMatch(handled.ContainingType.TypeArguments[0], message.Type))
                {
                    return member;
                }
            }

            fallback ??= member;

            foreach (var handlerInterface in HandlerInterfacesOf(handler))
            {
                if (!MessagesMatch(handlerInterface.Message.Type, message.Type))
                    continue;

                foreach (var declared in handlerInterface.Interface.GetMembers("Handle"))
                {
                    if (SymbolEqualityComparer.Default.Equals(
                            handler.FindImplementationForInterfaceMember(declared), member))
                    {
                        return member;
                    }
                }
            }

            if (OverridesHandlerBase(member, types))
                return member;
        }

        return fallback;
    }

    private static bool IsNamedHandle(IMethodSymbol method) =>
        method.Name is "Handle" || method.Name.EndsWith(".Handle", StringComparison.Ordinal);

    /// <summary>
    /// Exact first, then open-generic, so a handler closed over the caller's type argument wins
    /// over the open definition it was constructed from but the open one is still found.
    /// </summary>
    public static bool MessagesMatch(ITypeSymbol? candidate, ITypeSymbol? wanted) =>
        candidate is not null && wanted is not null
        && (SymbolEqualityComparer.Default.Equals(candidate, wanted)
            || SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, wanted.OriginalDefinition));

    /// <summary>
    /// Whether this is one of the extension methods Zapto's generator emits.
    /// </summary>
    /// <remarks>
    /// The class name alone would not do: <c>SenderExtensions</c> is emitted into the message
    /// type's own namespace, which is arbitrary user code, so it is the <c>this</c> parameter that
    /// says the class is the generator's and not somebody's own helper of the same name.
    /// </remarks>
    public static bool IsGeneratedSenderExtension(IMethodSymbol method)
    {
        var definition = (method.ReducedFrom ?? method).OriginalDefinition;

        return definition is { IsStatic: true, IsExtensionMethod: true }
            && definition.ContainingType is { Name: MediatorTypes.SenderExtensionsName, IsStatic: true }
            && definition.Parameters.Length > 0
            && definition.Parameters[0].Type is INamedTypeSymbol receiver
            && NamespaceOf(receiver) == MediatorTypes.ZaptoNamespace
            && receiver.Name is "ISender" or "IPublisher" or "IBackgroundPublisher";
    }

    /// <summary>Whether the symbol is declared inside the generated registration class.</summary>
    public static bool IsGeneratedRegistration(ISymbol? symbol)
    {
        for (var type = symbol as INamedTypeSymbol ?? symbol?.ContainingType;
             type is not null;
             type = type.ContainingType)
        {
            if (type.Name == MediatorTypes.AssemblyExtensionsName
                && NamespaceOf(type) == MediatorTypes.ZaptoNamespace)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the symbol is declared inside a generated <c>SenderExtensions</c>.</summary>
    public static bool IsInsideSenderExtensions(ISymbol? symbol)
    {
        for (var type = symbol as INamedTypeSymbol ?? symbol?.ContainingType;
             type is not null;
             type = type.ContainingType)
        {
            if (type.Name == MediatorTypes.SenderExtensionsName)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The dispatch <paramref name="method"/> is, if it is one.
    /// </summary>
    /// <remarks>
    /// Which type argument carries the message is decided by its constraint and never by its
    /// position. MediatR's most-used overload is <c>Send&lt;TResponse&gt;(IRequest&lt;TResponse&gt;)</c>,
    /// where position zero is the <em>response</em> — reading it as the message would send every
    /// go-to-definition to the wrong type, silently, on the shape most code uses.
    /// </remarks>
    public static bool TryGetDispatch(IMethodSymbol method, MediatorTypes types, out MediatorDispatch dispatch)
    {
        dispatch = default;

        var definition = (method.ReducedFrom ?? method).OriginalDefinition;
        if (Array.IndexOf(s_dispatchMethodNames, definition.Name) < 0)
            return false;

        if (definition.ContainingType is not { } owner || !DeclaresDispatch(definition, owner, types))
            return false;

        var kind = definition.Name switch
        {
            "Publish" => MediatorMessageKind.Notification,
            "CreateStream" => MediatorMessageKind.StreamRequest,
            _ => MediatorMessageKind.Request,
        };

        var messageTypeParameter = definition.TypeParameters
            .FirstOrDefault(tp => tp.ConstraintTypes.Any(IsMessageMarker));

        var messageParameter = definition.Parameters.FirstOrDefault(p =>
            !IsMediatorNamespaceType(p.Type, types)
            && (IsMessageMarker(p.Type) || p.Type.SpecialType == SpecialType.System_Object));

        dispatch = new MediatorDispatch(
            definition,
            kind,
            FlavorOf(owner) is var flavor && flavor != MediatorFlavor.None ? flavor : FlavorOfOwner(owner, types),
            messageTypeParameter,
            messageParameter);

        return true;
    }

    private static bool IsMediatorNamespaceType(ITypeSymbol type, MediatorTypes types) =>
        types.MediatorNamespaceType is { } ns && SymbolEqualityComparer.Default.Equals(ns, type);

    /// <summary>
    /// Whether the method is a dispatch interface's own member, or a class's implementation of one.
    /// The second case is what makes a call through a concrete mediator, or through a project's own
    /// wrapper, count the same as a call through the interface.
    /// </summary>
    private static bool DeclaresDispatch(IMethodSymbol definition, INamedTypeSymbol owner, MediatorTypes types)
    {
        if (types.DispatchInterfaces.Any(i =>
                SymbolEqualityComparer.Default.Equals(i, owner.OriginalDefinition)))
        {
            return true;
        }

        foreach (var implemented in owner.AllInterfaces)
        {
            if (!types.DispatchInterfaces.Any(i =>
                    SymbolEqualityComparer.Default.Equals(i, implemented.OriginalDefinition)))
            {
                continue;
            }

            foreach (var member in implemented.GetMembers(definition.Name))
            {
                if (SymbolEqualityComparer.Default.Equals(
                        owner.FindImplementationForInterfaceMember(member)?.OriginalDefinition, definition))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static MediatorFlavor FlavorOfOwner(INamedTypeSymbol owner, MediatorTypes types)
    {
        foreach (var implemented in owner.AllInterfaces)
        {
            if (types.DispatchInterfaces.Any(i =>
                    SymbolEqualityComparer.Default.Equals(i, implemented.OriginalDefinition)))
            {
                return FlavorOf(implemented);
            }
        }

        return MediatorFlavor.None;
    }

    /// <summary>
    /// The name Zapto's generator gives the extension method for a message.
    /// </summary>
    /// <remarks>
    /// A copy of the generator's own rule, kept only as a fallback for when the generated document
    /// cannot be read — a stale build, or a <c>SenderExtensions</c> partial somebody wrote by hand.
    /// Nothing decides a navigation on this alone, because two messages in one namespace can
    /// compute to the same name: <c>CreateUserRequest</c> and <c>CreateUserNotification</c> are both
    /// <c>CreateUserAsync</c>.
    /// </remarks>
    public static string ComputeExtensionName(MediatorMessageKind kind, string typeName, bool voidReturn)
    {
        string suffix = kind switch
        {
            MediatorMessageKind.Notification => "Notification",
            MediatorMessageKind.StreamRequest => "StreamRequest",
            _ => "Request",
        };

        string baseName =
            typeName.EndsWith(suffix, StringComparison.Ordinal) && typeName.Length != suffix.Length
                ? typeName[..^suffix.Length]
                : typeName;

        return voidReturn ? baseName : baseName + "Async";
    }
}

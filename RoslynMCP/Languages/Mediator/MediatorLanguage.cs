using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace RoslynMCP.Languages.Mediator;

/// <summary>
/// MediatR and Zapto.Mediator: the edge between a <c>Send</c> and the handler it reaches, which
/// Roslyn cannot see because the mediator resolves it through DI by matching generic types.
/// </summary>
/// <remarks>
/// <para>
/// The pack every other pack is not. There is no mediator file type — a request, its handler and
/// the call that joins them are all ordinary C# — so this owns no documents, answers no request
/// about a file, and exists entirely as contributions to requests about C#.
/// </para>
/// <para>
/// Both libraries at once rather than one pack each. They share the marker interfaces outright
/// (Zapto.Mediator takes them from the MediatR.Contracts package) and share every simple name and
/// arity besides, so the two differ only by namespace, and a solution can reference both. One pack
/// resolving both is a namespace check; two packs would be two copies of the same engine
/// disagreeing about a project that uses each.
/// </para>
/// </remarks>
internal sealed partial class MediatorLanguage : ILanguagePack
{
    /// <summary>
    /// The pack id, the <c>roslynSense.languages.*</c> key and the <c>tools.mediator</c> gate, one
    /// string so a new surface cannot spell it differently from the last one.
    /// </summary>
    public const string PackId = "mediator";

    public string Id => PackId;

    public string DisplayName => "Mediator";

    /// <summary>
    /// None, and that is the point. A dispatch and its handler are both <c>.cs</c>, so there is
    /// nothing for the extension-based routing to match on and every provider interface would be
    /// unreachable; the contributor lookup does not consult extensions, so an empty list costs
    /// nothing and keeps the pack out of every route it could only answer wrongly.
    /// </summary>
    public ImmutableArray<string> FileExtensions { get; } = [];

    /// <summary>
    /// Nothing to declare: no file type means no trigger characters, no commands, no globs and no
    /// tokens of its own. The lens the pack contributes is over a C# document and rides the
    /// existing C# lens registration.
    /// </summary>
    public LanguageCapabilities Capabilities => LanguageCapabilities.None;

    /// <summary>
    /// <c>MediatR.IBaseRequest</c> is the load-bearing one: it comes from the MediatR.Contracts
    /// package, which both libraries depend on, so a project that cannot resolve it has neither.
    /// The two dispatchers are listed beside it because a project resolving only the contracts
    /// declares messages but can neither send nor handle one, and there is nothing there to find.
    /// </summary>
    public ImmutableArray<string> WellKnownTypeNames { get; } =
    [
        "MediatR.IBaseRequest",
        "MediatR.ISender",
        "Zapto.Mediator.ISender",
    ];

    /// <summary>
    /// A handler is a type, its <c>Handle</c> and every dispatch method are methods. A caret on
    /// anything else — a field, a property, a local — cannot be either end of a dispatch.
    /// </summary>
    public ImmutableArray<SymbolKind> InterestingSymbolKinds { get; } =
        [SymbolKind.NamedType, SymbolKind.Method];

    /// <summary>
    /// Never. Zapto's generator emits real source-generated documents that Roslyn owns and the
    /// C# handlers answer about; this pack invents no C# of its own.
    /// </summary>
    public bool IsProjectionPath(string? filePath) => false;
}

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMCP.Config;
using RoslynMCP.Languages.Routes.Core;

namespace RoslynMCP.Languages.Routes;

/// <summary>
/// The HTTP endpoints a solution serves.
/// </summary>
/// <remarks>
/// <para>
/// A pack that owns no files and answers no request about one. What it contributes is a section of
/// the Discovery view, for the same reason the scheduled-jobs pack does: an endpoint is declared by
/// an attribute on a method or by a call in a startup file, so nothing in a file tree stands for
/// the route table, and "what does this service expose, and where is it handled" is a question the
/// editor has no answer to at all. Finding out means knowing which file the registrations happen to
/// be in, or reading every controller.
/// </para>
/// <para>
/// The two declaration styles are equally first-class. Attribute routing puts half the path on the
/// type and half on the method; minimal APIs put half in a group and half in the call. Both are
/// read, and a solution part-way through migrating from one to the other — which is most solutions
/// that have both — gets one list rather than two halves.
/// </para>
/// </remarks>
internal sealed partial class RoutesLanguage : ILanguagePack
{
    /// <summary>
    /// The pack id, the <c>roslynSense.languages.*</c> key and the <c>tools.routes</c> gate, one
    /// string so a new surface cannot spell it differently from the last one.
    /// </summary>
    public const string PackId = "routes";

    public RoutesLanguage(EffectiveSettings settings)
        : this(settings.Routes)
    {
    }

    /// <summary>The settings directly, for the hosts and the tests that have already resolved them.</summary>
    internal RoutesLanguage(RoutesSettings settings)
    {
        Settings = settings;
        Endpoints = new RouteIndex(settings);
    }

    internal RoutesSettings Settings { get; }

    /// <summary>
    /// The endpoints found in each compilation, memoized for this pack's lifetime.
    /// </summary>
    /// <remarks>
    /// Owned by the pack rather than static, because what it finds depends on the configured
    /// bindings — and a pack is a singleton per host holding one resolved settings, so this is the
    /// narrowest scope the answer is actually valid in.
    /// </remarks>
    internal RouteIndex Endpoints { get; }

    public string Id => PackId;

    public string DisplayName => "HTTP routes";

    /// <summary>
    /// None. A route is written in a <c>.cs</c> file, which the C# routes already cover.
    /// </summary>
    public ImmutableArray<string> FileExtensions { get; } = [];

    /// <summary>Nothing to declare: the pack contributes a section and no editor feature.</summary>
    public LanguageCapabilities Capabilities => LanguageCapabilities.None;

    /// <summary>
    /// The web frameworks. Declared for completeness rather than as a gate — the section is gated
    /// by <see cref="RouteProjectProbe"/>, which answers before anything is compiled, and by a
    /// configured binding, which names a routing layer that resolves none of these.
    /// </summary>
    public ImmutableArray<string> WellKnownTypeNames { get; } = RoutePresets.WellKnownTypes;

    /// <summary>No contributor pass over C# symbols has anything to add to a route.</summary>
    public ImmutableArray<SymbolKind> InterestingSymbolKinds { get; } = [];

    /// <summary>Nothing is projected: the route is read where it is written.</summary>
    public bool IsProjectionPath(string? filePath) => false;
}

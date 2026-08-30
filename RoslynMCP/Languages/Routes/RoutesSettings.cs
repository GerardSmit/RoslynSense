using System.Collections.Frozen;
using System.Collections.Immutable;
using RoslynMCP.Config;
using RoslynMCP.Languages.Routes.Core;

namespace RoslynMCP.Languages.Routes;

/// <summary>
/// What this process treats as a declaration of an HTTP endpoint, after the configuration has been
/// read and checked.
/// </summary>
/// <remarks>
/// An absent section is not the same as a disabled pack: the shipped tables cover ASP.NET Core and
/// Web API, so the pack is useful with nothing configured at all. What configuration adds is the
/// routing layer a solution wrote for itself.
/// <para>
/// A malformed entry warns and is dropped rather than failing the load, the same as every other
/// pack's settings — a typo in one binding must not cost the solution its Routes section.
/// </para>
/// </remarks>
internal sealed record RoutesSettings
{
    /// <summary><c>--no-routes</c>, or <c>tools.routes: false</c>.</summary>
    public static RoutesSettings Disabled { get; } = new() { Enabled = false };

    /// <summary>The shipped tables alone, which is what an unconfigured solution gets.</summary>
    public static RoutesSettings Default { get; } = new()
    {
        Enabled = true,
        Attributes = RoutePresets.Attributes,
        Methods = RoutePresets.Methods,
    };

    private readonly ImmutableArray<RouteAttributeBinding> _attributes = [];
    private readonly ImmutableArray<RouteMethodBinding> _methods = [];

    public required bool Enabled { get; init; }

    /// <summary>The shipped attributes, then the user's.</summary>
    /// <remarks>
    /// The name gate beside it is derived here rather than memoised on first use, which matters
    /// because this is a record: a <c>with</c> expression copies fields, so a lazily-filled cache
    /// would survive into a copy that no longer matches it — and the copy that changes the bindings
    /// is exactly the one a configured solution runs on.
    /// </remarks>
    public ImmutableArray<RouteAttributeBinding> Attributes
    {
        get => _attributes;
        init
        {
            _attributes = value;
            AttributeNames = value
                .Select(binding => RouteNames.Bare(binding.AttributeName))
                .ToFrozenSet(StringComparer.Ordinal);
        }
    }

    /// <summary>The shipped registration calls, then the user's.</summary>
    public ImmutableArray<RouteMethodBinding> Methods
    {
        get => _methods;
        init
        {
            _methods = value;
            MethodNames = value
                .Select(binding => binding.MemberName)
                .ToFrozenSet(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Whether the user has named a declaration of their own.
    /// </summary>
    /// <remarks>
    /// Asked because a configured binding names an in-house routing layer, and the project
    /// declaring one references no web framework — that is what made it in-house. So configuration
    /// widens which projects are looked at, exactly as it does for scheduled jobs. It works
    /// because <see cref="Resolve"/> is additive and keeps the shipped tables in front.
    /// </remarks>
    public bool IsConfigured =>
        Attributes.Length > RoutePresets.Attributes.Length
        || Methods.Length > RoutePresets.Methods.Length;

    /// <summary>
    /// The attribute names worth binding, without the <c>Attribute</c> suffix.
    /// </summary>
    /// <remarks>
    /// The syntax gate: an attribute's name is written in the source, so the semantic model is
    /// asked about the handful that could be routes rather than the hundreds that are not. Both
    /// spellings collapse to one entry here, and the written name is stripped to match.
    /// </remarks>
    public FrozenSet<string> AttributeNames { get; private init; } = FrozenSet<string>.Empty;

    /// <summary>The method names worth binding, the same gate for registration calls.</summary>
    public FrozenSet<string> MethodNames { get; private init; } = FrozenSet<string>.Empty;

    /// <summary>
    /// What a source file that declares an endpoint has written in it, as plain text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The evidence of last resort for <see cref="Core.RouteProjectProbe"/>. A project that writes
    /// its own routing layer references no web framework — that is what made it in-house — so its
    /// manifests say nothing, and its controllers are found only by reading the source.
    /// </para>
    /// <para>
    /// The punctuation is what makes this a marker rather than a word. <c>Route</c> appears in
    /// <c>RouteData</c>, in a local named <c>route</c> and in a comment; <c>[Route</c> is an
    /// attribute being applied. Both spellings collapse onto one marker, since
    /// <c>[RouteAttribute(</c> starts with <c>[Route</c>.
    /// </para>
    /// </remarks>
    public ImmutableArray<string> SourceMarkers =>
    [
        .. AttributeNames.Select(name => $"[{name}"),
        .. MethodNames.Select(name => $".{name}("),
    ];

    public static RoutesSettings Resolve(bool enabled, RoutesConfig? config, List<string> warnings)
    {
        if (!enabled)
            return Disabled;

        if (config is null)
            return Default;

        return new RoutesSettings
        {
            Enabled = true,
            Attributes = [.. RoutePresets.Attributes, .. ReadAttributes(config.Attributes, warnings)],
            Methods = [.. RoutePresets.Methods, .. ReadMethods(config.Methods, warnings)],
        };
    }

    private static ImmutableArray<RouteAttributeBinding> ReadAttributes(
        IReadOnlyList<RouteAttributeEntry>? configured, List<string> warnings)
    {
        if (configured is not { Count: > 0 })
            return [];

        var bindings = ImmutableArray.CreateBuilder<RouteAttributeBinding>(configured.Count);

        foreach (var entry in configured)
        {
            if (entry.AttributeName is not { Length: > 0 } name || string.IsNullOrWhiteSpace(name))
            {
                warnings.Add("routes.attributes: an entry has no attributeName; skipped.");
                continue;
            }

            if (entry.PathIndex is < 0)
            {
                warnings.Add(
                    $"routes.attributes for '{name}': pathIndex {entry.PathIndex} is not an "
                    + "argument position; the first string argument is used instead.");
            }

            bindings.Add(new RouteAttributeBinding
            {
                AttributeName = name.Trim(),
                ContainingType = string.IsNullOrWhiteSpace(entry.ContainingType)
                    ? null
                    : entry.ContainingType,
                PathIndex = entry.PathIndex is >= 0 ? entry.PathIndex : null,
                Verb = ReadVerb(entry.Verb, $"routes.attributes for '{name}'", warnings),
            });
        }

        return bindings.ToImmutable();
    }

    private static ImmutableArray<RouteMethodBinding> ReadMethods(
        IReadOnlyList<RouteMethodEntry>? configured, List<string> warnings)
    {
        if (configured is not { Count: > 0 })
            return [];

        var bindings = ImmutableArray.CreateBuilder<RouteMethodBinding>(configured.Count);

        foreach (var entry in configured)
        {
            if (entry.MemberName is not { Length: > 0 } name || string.IsNullOrWhiteSpace(name))
            {
                warnings.Add("routes.methods: an entry has no memberName; skipped.");
                continue;
            }

            bindings.Add(new RouteMethodBinding
            {
                MemberName = name.Trim(),
                ContainingType = string.IsNullOrWhiteSpace(entry.ContainingType)
                    ? null
                    : entry.ContainingType,
                ParameterTypes = entry.ParameterTypes is { } types ? [.. types] : null,
                PathIndex = entry.PathIndex is >= 0 ? entry.PathIndex : null,
                HandlerIndex = entry.HandlerIndex is >= 0 ? entry.HandlerIndex : null,
                Verb = ReadVerb(entry.Verb, $"routes.methods for '{name}'", warnings),
                Kind = ReadKind(entry.Kind, name, warnings),
            });
        }

        return bindings.ToImmutable();
    }

    /// <summary>
    /// The verb, upper-cased.
    /// </summary>
    /// <remarks>
    /// Not checked against a list of the methods HTTP defines. <c>PROPFIND</c> and a house verb
    /// are both things a service really answers to, and refusing one would be this pack claiming to
    /// know the protocol better than the code in front of it.
    /// </remarks>
    private static string? ReadVerb(string? configured, string where, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return null;

        string verb = configured.Trim();

        if (verb.Contains(' ', StringComparison.Ordinal))
        {
            warnings.Add($"{where}: verb '{configured}' has a space in it; ignored.");
            return null;
        }

        return verb.ToUpperInvariant();
    }

    private static RouteCallKind ReadKind(string? configured, string member, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return RouteCallKind.Endpoint;

        switch (configured.Trim().ToLowerInvariant())
        {
            case "endpoint":
                return RouteCallKind.Endpoint;
            case "group":
                return RouteCallKind.Group;
            default:
                warnings.Add(
                    $"routes.methods for '{member}': kind '{configured}' is not endpoint or group; "
                    + "using endpoint.");
                return RouteCallKind.Endpoint;
        }
    }
}

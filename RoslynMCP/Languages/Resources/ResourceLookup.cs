using System.Collections.Immutable;

namespace RoslynMCP.Languages.Resources;

/// <summary>Where the root value is read from at a call site. Syntax only.</summary>
/// <remarks>
/// Kept apart from <see cref="RootInterpretation"/> on purpose. Fusing the two into a single
/// <c>resourceClass</c>-style enum yields members like <c>VirtualPathFromContainingType</c> and a
/// set that grows multiplicatively; as a cross product the same six sources cover DNN, stock
/// ASP.NET and <c>IStringLocalizer</c> without any member being invented twice.
/// </remarks>
internal enum RootSource
{
    /// <summary>A positional argument — <c>GetString(key, "~/DesktopModules/…/View.ascx")</c>.</summary>
    Argument,

    /// <summary>A generic type argument — <c>IStringLocalizer&lt;Home&gt;</c>.</summary>
    TypeArgument,

    /// <summary>The type the call sits in — <c>PortalModuleBase.LocalizeText(key)</c>.</summary>
    ContainingType,

    /// <summary>The file the call sits in — <c>&lt;%$ dnnLoc:Key %&gt;</c>, <c>meta:resourcekey</c>.</summary>
    ContainingFile,

    /// <summary>A constant baked into the lookup itself, for a helper that always reads one file.</summary>
    Constant,

    /// <summary>No root at all; the key is global.</summary>
    None,
}

/// <summary>How a root value becomes a resx base name. Semantics only.</summary>
internal enum RootInterpretation
{
    /// <summary>An ASP.NET virtual path — <c>~/…/View.ascx</c> maps to the markup file, and from
    /// there to its <c>App_LocalResources</c> sibling.</summary>
    VirtualPath,

    /// <summary>A global resource class name — the <c>Strings</c> in
    /// <c>&lt;%$ Resources: Strings, Title %&gt;</c>, resolved under <c>App_GlobalResources</c>.</summary>
    GlobalClassName,

    /// <summary>A CLR type name, resolved the way <c>ResourceManager</c> resolves one.</summary>
    TypeName,

    /// <summary>A path relative to the call site's own directory.</summary>
    RelativePath,

    /// <summary>Already a resx base name; used as written.</summary>
    BaseName,
}

/// <summary>
/// A call shape that carries a resource key, and where its root comes from.
/// </summary>
/// <remarks>
/// <see cref="ParameterTypes"/> is mandatory rather than decorative.
/// <c>Localization.GetString</c> has three distinct two-argument overloads —
/// <c>(string, string)</c>, <c>(string, Control)</c> and <c>(string, PortalSettings)</c> — and only
/// one of them puts a root at index 1. Matching on name and arity binds all three and resolves
/// garbage for two of them.
/// </remarks>
internal sealed record ResourceLookup
{
    /// <summary>
    /// Fully-qualified name of the type declaring the member, or null to match on the name and
    /// signature alone.
    /// </summary>
    /// <remarks>
    /// Omitting it is the escape hatch for a codebase that wraps localization per module: a page
    /// declaring its own <c>protected string GetString(string key)</c> is declared by nothing the
    /// configuration can name, and one such wrapper per module is a list nobody will keep current.
    /// It is deliberately not the default — every preset lookup names its type, because a bare
    /// name matches any method called <c>GetString</c> in the solution, including one that has
    /// nothing to do with resources.
    /// </remarks>
    public string? ContainingType { get; init; }

    public required string MethodName { get; init; }

    /// <summary>Positional parameter type names that must match. Null matches any arity;
    /// <c>"*"</c> matches one parameter.</summary>
    public ImmutableArray<string>? ParameterTypes { get; init; }

    public required int KeyIndex { get; init; }

    public required RootSource RootSource { get; init; }

    public required RootInterpretation RootInterpretation { get; init; }

    public int RootIndex { get; init; }

    public string? RootConstant { get; init; }

    /// <summary>Appended when the key contains no <c>'.'</c>. DNN's <c>".Text"</c> — and the
    /// condition it applies is <c>IndexOf('.') &lt; 1</c>, so a leading dot gets the suffix
    /// too.</summary>
    public string? DefaultKeySuffix { get; init; }

    /// <summary>Root convention ids tried in order when the key misses.</summary>
    public ImmutableArray<string> Fallbacks { get; init; } = [];
}

/// <summary>A named way of turning a call-site file into a resx base name.</summary>
internal sealed record ResourceRootConvention
{
    public required string Id { get; init; }

    /// <summary>Relative to the call site's directory — <c>App_LocalResources</c>.</summary>
    public string? SiblingFolder { get; init; }

    /// <summary>Relative to the project root — <c>App_GlobalResources</c>. Exclusive with
    /// <see cref="SiblingFolder"/>.</summary>
    public string? RootFolder { get; init; }

    /// <summary><c>SharedResources</c>; null derives the name from the call-site file.</summary>
    public string? FixedName { get; init; }

    public ImmutableArray<string> Suffix { get; init; } = [".resx"];
}

/// <summary>
/// A customization segment that sits beside the base file — DNN's <c>.Host</c> and
/// <c>.Portal-{id}</c>.
/// </summary>
/// <remarks>
/// Higher rank wins: base = 0, Host = 1, Portal-* = 2. The rank is explicit rather than derived
/// from the order of the pattern list because alphabetical ordering of
/// <c>["Portal-*", "Host"]</c> gets the precedence backwards.
/// </remarks>
internal sealed record ResourceOverrideRule(string Pattern, int Rank);

/// <summary>
/// How sure we are of the base name a call site resolves to.
/// </summary>
/// <remarks>
/// Confidence gates features rather than decorating them. A false "key does not exist" on a key
/// that resolves fine at runtime is what gets a feature switched off, and a rename applied across a
/// guessed file set is silent corruption.
/// </remarks>
internal enum RootConfidence
{
    /// <summary>A literal, a constant, or a convention DNN itself applies — everything on.</summary>
    Exact,

    /// <summary>A single assignment with a constant right-hand side — everything on.</summary>
    Inferred,

    /// <summary>Proximity candidates: navigation, hover and completion on and capped;
    /// diagnostics off; rename refused.</summary>
    Ambiguous,

    /// <summary>Nothing reachable — empty results, and no diagnostic.</summary>
    Unknown,
}

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
/// A key that names a control rather than being written out: the application's own localizer
/// composes it from a markup attribute, so nothing in the solution ever spells the key.
/// </summary>
/// <remarks>
/// <para>
/// The shape every WebForms localization walker ends up with. Rather than each page asking for its
/// own strings, one pass over the control tree asks for each control under its <c>ID</c> — DNN's
/// default property turns that into <c>{ID}.Text</c> — and a grid asks for one heading per column
/// under a prefix and the column's <c>UniqueName</c>, because a column is not a control and has no
/// ID to be found by. A key written that way has no call site to find: the only thing in the
/// solution that mentions it is an <c>ID=</c> or a <c>UniqueName=</c> that does not look like a
/// resource key at all.
/// </para>
/// <para>
/// Written as the key it produces, with the attribute in the middle —
/// <c>Header[Control.UniqueName].Text</c>. A prefix-only rule would have covered the two shapes
/// that prompted this and nothing else: a codebase that settled on <c>[Control.ID].Header</c>
/// instead puts its fixed part on the other side, and one that appends the property elsewhere puts
/// it in both. Naming the whole key spares anyone from having to recognise which half of theirs is
/// which.
/// </para>
/// <para>
/// Declared by attribute rather than by control type, because the rule is not about control types.
/// Every grid column kind — bound, template, button — carries <c>UniqueName</c>, and a rule naming
/// any one vendor's control would miss the rest while pretending to be general.
/// </para>
/// </remarks>
internal sealed record ResourceMarkupBinding
{
    /// <summary>What the key starts with, before the attribute's value. Often empty.</summary>
    public string Prefix { get; init; } = "";

    /// <summary>The markup attribute holding the middle — <c>ID</c>, <c>UniqueName</c>. Matched
    /// case-insensitively, as markup attribute names are.</summary>
    public required string Attribute { get; init; }

    /// <summary>What the key ends with — the property the localizer sets, usually.</summary>
    public string Suffix { get; init; } = "";

    /// <summary>
    /// The attribute value this pattern would have had to read to produce <paramref name="key"/>,
    /// or null if it could not have produced it at all.
    /// </summary>
    /// <remarks>
    /// The whole match, run before anything is opened: a key that no pattern could have composed
    /// costs a couple of string comparisons rather than a parse of the page beside it. Ordinal,
    /// because a resource key is compared ordinally everywhere else and an id differing only in
    /// case would not have bound at runtime either.
    /// </remarks>
    public string? Middle(string key) =>
        key.Length > Prefix.Length + Suffix.Length
        && key.StartsWith(Prefix, StringComparison.Ordinal)
        && key.EndsWith(Suffix, StringComparison.Ordinal)
            ? key[Prefix.Length..^Suffix.Length]
            : null;

    /// <summary>
    /// Reads the configured form, <c>Header[Control.UniqueName].Text</c>, or explains what is wrong
    /// with it.
    /// </summary>
    /// <remarks>
    /// The <c>Control.</c> in front of the attribute name is optional and ignored. It is there
    /// because that is how the shape reads to someone who has one of these in their codebase, and
    /// dropping it silently is friendlier than rejecting a pattern over punctuation.
    /// </remarks>
    public static ResourceMarkupBinding? Parse(string pattern, out string? problem)
    {
        problem = null;

        int open = pattern.IndexOf('[', StringComparison.Ordinal);
        int close = open < 0 ? -1 : pattern.IndexOf(']', open + 1);

        if (open < 0 || close < 0)
        {
            problem = "it names no attribute; expected something like 'Header[Control.UniqueName].Text'";
            return null;
        }

        if (pattern.IndexOf('[', close + 1) >= 0)
        {
            problem = "it names more than one attribute; a key is composed from exactly one";
            return null;
        }

        string attribute = pattern[(open + 1)..close];

        if (attribute.StartsWith("Control.", StringComparison.OrdinalIgnoreCase))
            attribute = attribute["Control.".Length..];

        if (attribute.Length == 0)
        {
            problem = "the attribute name between the brackets is empty";
            return null;
        }

        return new ResourceMarkupBinding
        {
            Prefix = pattern[..open],
            Attribute = attribute,
            Suffix = pattern[(close + 1)..],
        };
    }
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

using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

/// <summary>
/// roslynSense/settingChoices — the values a setting can currently take, for the settings page's
/// dropdowns.
/// </summary>
/// <remarks>
/// <para>
/// Some settings are a closed list the schema can spell out; others are a list only the running
/// configuration knows. A lookup's <c>fallbacks</c> names root conventions, and which conventions
/// exist is the preset plus whatever the file declared — a question with an answer per solution,
/// not per schema.
/// </para>
/// <para>
/// <paramref name="Config"/> is the panel's own merge of every layer rather than the config the
/// server was started with, so the answer reflects what is on screen. A convention someone has
/// just added should be offerable as a fallback before the file is saved and the server has
/// reloaded.
/// </para>
/// </remarks>
/// <param name="Path">The setting's dotted path, item markers included —
/// <c>resources.lookups[].fallbacks</c>.</param>
public sealed record SettingChoicesParams(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("config")] System.Text.Json.JsonElement? Config = null);

/// <param name="Detail">A short right-hand note — what the value means here, not what it means in
/// general.</param>
public sealed record SettingChoice(
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("detail")] string? Detail = null);

public sealed record SettingChoicesResult(
    [property: JsonPropertyName("items")] SettingChoice[] Items);

/// <summary>
/// roslynSense/memberShape — which members of the loaded solution a configured type/name/signature
/// triple actually selects.
/// </summary>
/// <remarks>
/// <para>
/// A settings page cannot ask someone to write <c>DotNetNuke.Services.Localization.Localization</c>
/// into a blank text box and hope. This is the same three fields, answered against the real type
/// system: what types the fragment could mean, what members that type declares, and — once both
/// are filled in — the overloads the signature selects, each with its parameters named, so that
/// choosing which one carries the key is a click rather than an off-by-one.
/// </para>
/// <para>
/// Deliberately not resources-specific. The triple is "a class, a member on it, and a position in
/// its parameter list", which is what every setting naming a call shape needs.
/// </para>
/// </remarks>
/// <param name="ContainingType">Fully-qualified, or a fragment to complete. Empty asks only for
/// type suggestions.</param>
/// <param name="MemberName">The member name, or <c>Item</c> for an indexer. Empty asks only for
/// the member names the type offers.</param>
/// <param name="ParameterTypes">Positional type names that must match, <c>"*"</c> for one
/// parameter of any type. Null matches any arity.</param>
public sealed record MemberShapeParams(
    [property: JsonPropertyName("containingType")] string? ContainingType = null,
    [property: JsonPropertyName("memberName")] string? MemberName = null,
    [property: JsonPropertyName("parameterTypes")] string[]? ParameterTypes = null,
    [property: JsonPropertyName("maxResults")] int MaxResults = 20);

public sealed record MemberShapeParameter(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type);

/// <param name="DeclaredBy">The type that declares the member, which is not always the type asked
/// about: a lookup names the base class, and the members come from it.</param>
/// <param name="Matched">Whether the configured signature selects this overload. Overloads that
/// miss are still returned, because "there are three of these and you matched one" is the fact the
/// page exists to show.</param>
public sealed record MemberShapeMatch(
    [property: JsonPropertyName("declaredBy")] string DeclaredBy,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("signature")] string Signature,
    [property: JsonPropertyName("parameters")] MemberShapeParameter[] Parameters,
    [property: JsonPropertyName("matched")] bool Matched);

/// <param name="ResolvedType">The type name as C# spells it, once a fragment resolved to exactly
/// one type — what the field should be corrected to.</param>
/// <param name="Problem">Why there is nothing to show: no solution loaded, no such type, no such
/// member. Shown as written.</param>
public sealed record MemberShapeResult(
    [property: JsonPropertyName("typeSuggestions")] string[] TypeSuggestions,
    [property: JsonPropertyName("memberSuggestions")] string[] MemberSuggestions,
    [property: JsonPropertyName("matches")] MemberShapeMatch[] Matches,
    [property: JsonPropertyName("resolvedType")] string? ResolvedType = null,
    [property: JsonPropertyName("problem")] string? Problem = null);

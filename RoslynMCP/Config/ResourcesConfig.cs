namespace RoslynMCP.Config;

/// <summary>
/// The <c>resources</c> section of <c>roslynsense.json</c>: which <c>.resx</c> files are found,
/// how their names decompose, and which call shapes carry a resource key.
/// </summary>
/// <remarks>
/// <see cref="Preset"/> is the surface almost everyone should use — the DNN one alone carries
/// seven <c>GetString</c> overloads that have to be told apart by parameter type — and everything
/// else here layers on top of it, so a solution can take a preset and add the one helper its own
/// code wraps every lookup in. Raw <see cref="Lookups"/> is the documented escape hatch, not the
/// expected starting point.
/// </remarks>
public sealed class ResourcesConfig
{
    /// <summary>
    /// A built-in lookup set to start from: <c>webforms</c>, <c>dnn</c>, <c>dotnet</c>, or
    /// <c>none</c> for nothing but what this file declares. Omitted means all of them, which is
    /// safe because every built-in lookup is bound to a fully-qualified containing type that
    /// simply does not resolve in a solution built on something else.
    /// </summary>
    public string? Preset { get; init; }

    /// <summary>Globs relative to the project directory, discovery only. Empty means every
    /// <c>.resx</c> outside <c>bin</c> and <c>obj</c>.</summary>
    public IReadOnlyList<string>? Include { get; init; }

    /// <summary>Globs removed from what <see cref="Include"/> found, applied first.</summary>
    public IReadOnlyList<string>? Exclude { get; init; }

    /// <summary>Customization segments sitting beside the base file. Replaces the preset's set
    /// rather than adding to it — a rank scheme only means anything as a whole.</summary>
    public IReadOnlyList<ResourceOverrideConfig>? Overrides { get; init; }

    /// <summary>Merged into the preset's by <see cref="ResourceConventionConfig.Id"/>, so
    /// redeclaring one id replaces that convention and leaves the rest alone.</summary>
    public IReadOnlyList<ResourceConventionConfig>? Conventions { get; init; }

    /// <summary>Appended to the preset's.</summary>
    public IReadOnlyList<ResourceLookupConfig>? Lookups { get; init; }

    /// <summary>
    /// Whether a key that no file of its family declares is reported. Off by default.
    /// </summary>
    /// <remarks>
    /// DNN's dominant call shapes have no statically readable root, and a false "this key does not
    /// exist" on a key that resolves perfectly well at runtime is exactly what gets a feature
    /// switched off wholesale. The rule already refuses to run below
    /// <c>RootConfidence.Inferred</c>; this is the second gate, so a solution opts in once it has
    /// seen the navigation land where it should.
    /// </remarks>
    public bool MissingKeyDiagnostic { get; init; }
}

/// <summary>Higher <paramref name="Rank"/> wins; the uncustomized file is 0.</summary>
public sealed record ResourceOverrideConfig(string Pattern, int Rank);

/// <summary>A named way of turning a call-site file into a resx base name.</summary>
public sealed class ResourceConventionConfig
{
    public string? Id { get; init; }

    /// <summary>Relative to the call site's own directory — <c>App_LocalResources</c>.</summary>
    public string? SiblingFolder { get; init; }

    /// <summary>Relative to the project root — <c>App_GlobalResources</c>. Exclusive with
    /// <see cref="SiblingFolder"/>.</summary>
    public string? RootFolder { get; init; }

    /// <summary>A fixed file name such as <c>SharedResources</c>; omitted derives the name from
    /// the call-site file.</summary>
    public string? FixedName { get; init; }

    public IReadOnlyList<string>? Suffix { get; init; }
}

/// <summary>A call shape that carries a resource key, and where its root comes from.</summary>
public sealed class ResourceLookupConfig
{
    /// <summary>Fully-qualified name of the type declaring the member.</summary>
    public string? ContainingType { get; init; }

    /// <summary>The method name, or <c>Item</c> for an indexer.</summary>
    public string? MethodName { get; init; }

    /// <summary>Positional parameter type names that must match, <c>"*"</c> for one parameter of
    /// any type. Omitted matches any arity — which is wrong wherever a type has overloads that
    /// disagree about where the root sits.</summary>
    public IReadOnlyList<string>? ParameterTypes { get; init; }

    public int KeyIndex { get; init; }

    /// <summary>One of <c>argument</c>, <c>typeArgument</c>, <c>containingType</c>,
    /// <c>containingFile</c>, <c>constant</c>, <c>none</c>.</summary>
    public string? RootSource { get; init; }

    /// <summary>One of <c>virtualPath</c>, <c>globalClassName</c>, <c>typeName</c>,
    /// <c>relativePath</c>, <c>baseName</c>.</summary>
    public string? RootInterpretation { get; init; }

    public int RootIndex { get; init; }

    /// <summary>The root itself, for a helper that always reads one file.</summary>
    public string? RootConstant { get; init; }

    /// <summary>Appended when the key contains no <c>'.'</c> — DNN's <c>.Text</c>.</summary>
    public string? DefaultKeySuffix { get; init; }

    /// <summary>Convention ids tried in order when the key misses.</summary>
    public IReadOnlyList<string>? Fallbacks { get; init; }
}

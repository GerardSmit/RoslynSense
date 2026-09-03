using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace RoslynMCP.Config;

/// <summary>Where one <c>roslynsense.json</c> came from, weakest scope first.</summary>
/// <remarks>
/// The order of the members is the precedence order, and <see cref="ConfigLayerLoader"/> relies on
/// it: a stronger scope's value replaces a weaker one field by field.
/// </remarks>
public enum ConfigScope
{
    /// <summary>
    /// <c>~/.roslynsense/roslynsense.json</c> — one file per machine, for the settings a person
    /// wants everywhere and would otherwise paste into every repository.
    /// </summary>
    Global,

    /// <summary>
    /// A <c>roslynsense.json</c> in the working directory or an ancestor of it: the team's
    /// settings, committed with the code. Several can apply at once, outermost first.
    /// </summary>
    Repo,

    /// <summary>
    /// A <c>roslynsense.local.json</c> beside a <see cref="Repo"/> file: one person's overrides for
    /// this checkout, meant to be gitignored rather than committed.
    /// </summary>
    RepoLocal,

    /// <summary>
    /// <c>~/.roslynsense/projects/&lt;mangled-path&gt;/roslynsense.json</c> — the same personal
    /// overrides for a checkout the person cannot or would rather not write into.
    /// </summary>
    Personal,
}

/// <summary>One file considered while resolving the configuration, whether or not it exists.</summary>
/// <param name="Scope">Which layer the file belongs to.</param>
/// <param name="FilePath">Where the file is, or would be if it were created.</param>
/// <param name="Json">Its parsed contents, or null when it does not exist or did not parse.</param>
/// <param name="LoadError">Why it did not parse, or null.</param>
/// <remarks>
/// Non-existent layers are reported too, deliberately: "where would I put this setting so it
/// applies only to me" is a question the settings UI answers from this list, and a layer that is
/// merely absent is a perfectly good answer to it.
/// </remarks>
public sealed record ConfigLayer(
    ConfigScope Scope,
    string FilePath,
    JsonObject? Json = null,
    string? LoadError = null)
{
    public bool Exists => Json is not null;
}

/// <summary>The configuration every layer merged together, and the layers it came from.</summary>
/// <param name="Config">The merged settings, or null when nothing was found and nothing parsed.</param>
/// <param name="Layers">Every candidate file, weakest first. Includes the ones that do not exist.</param>
/// <param name="PrimaryPath">
/// The file to name when only one can be named — the strongest one that exists, which is the one
/// an edit most likely landed in. Null when no layer exists.
/// </param>
/// <param name="LoadError">
/// The first parse failure, if any. A layer that does not parse is skipped rather than fatal: the
/// other layers are still a better answer than no configuration at all.
/// </param>
public sealed record LayeredConfig(
    RoslynSenseConfig? Config,
    ImmutableArray<ConfigLayer> Layers,
    string? PrimaryPath,
    string? LoadError)
{
    /// <summary>Every layer that actually exists, weakest first.</summary>
    public IEnumerable<ConfigLayer> Present => Layers.Where(layer => layer.Exists);

    /// <summary>
    /// The layers merged, still as JSON — before anything was bound to <see cref="Config"/>.
    /// </summary>
    /// <remarks>
    /// The only view in which "no layer mentioned this setting" is still visible, which is what the
    /// merge rule is actually about. Null when no layer parsed.
    /// </remarks>
    public JsonObject? MergedJson { get; init; }
}

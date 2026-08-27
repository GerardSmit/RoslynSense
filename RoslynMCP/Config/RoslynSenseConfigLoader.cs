using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RoslynMCP.Config;

public static class RoslynSenseConfigLoader
{
    public const string FileName = "roslynsense.json";

    /// <summary>The personal sibling of <see cref="FileName"/>, meant to be gitignored.</summary>
    public const string LocalFileName = "roslynsense.local.json";

    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// The reader a layer is parsed with. Public so that anything reading a fragment of the same
    /// file — the settings page asking what a half-written section would resolve to — reads it
    /// under the same rules rather than a stricter set of its own.
    /// </summary>
    public static JsonSerializerOptions SerializerOptions => s_options;

    private static readonly JsonDocumentOptions s_documentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// The configuration in effect for a working directory, merged from every layer that applies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kept as the shape it always had — one config, one path to name, one error — so the callers
    /// that only want the answer do not have to care that there is now more than one file behind
    /// it. <see cref="LoadLayers"/> is the same walk with the layers still visible.
    /// </para>
    /// </remarks>
    public static (RoslynSenseConfig? Config, string? FilePath, string? LoadError) Load(string startDir)
    {
        var layered = LoadLayers(startDir);
        return (layered.Config, layered.PrimaryPath, layered.LoadError);
    }

    /// <summary>
    /// Every <c>roslynsense.json</c> that applies to a working directory, merged weakest-first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order is: the global file in the home directory; then each repository file from the
    /// filesystem root down to <paramref name="startDir"/>, each immediately followed by its
    /// <c>roslynsense.local.json</c> sibling; then the personal file the home directory keeps for
    /// this working directory. Nearest wins, and personal beats shared at the same distance.
    /// </para>
    /// <para>
    /// Merging happens on the raw JSON rather than on <see cref="RoslynSenseConfig"/>, which is
    /// what makes "the layer never mentioned this setting" different from "the layer set it to
    /// what the default happens to be". Objects merge key by key, everything else replaces —
    /// including arrays, because a layer that lists two preload paths means those two and not
    /// those two appended to someone else's. An explicit <c>null</c> replaces as well, which is
    /// how a nearer layer puts a setting back to its default.
    /// </para>
    /// </remarks>
    public static LayeredConfig LoadLayers(string startDir)
    {
        var layers = ImmutableArray.CreateBuilder<ConfigLayer>();

        if (ConfigPaths.GlobalConfigFile is { } globalFile)
            layers.Add(ReadLayer(ConfigScope.Global, globalFile));

        foreach (string directory in RepositoryDirectories(startDir))
        {
            layers.Add(ReadLayer(ConfigScope.Repo, Path.Combine(directory, FileName)));
            layers.Add(ReadLayer(ConfigScope.RepoLocal, Path.Combine(directory, LocalFileName)));
        }

        if (ConfigPaths.PersonalConfigFile(startDir) is { } personalFile)
            layers.Add(ReadLayer(ConfigScope.Personal, personalFile));

        return Merge(layers.ToImmutable());
    }

    /// <summary>
    /// The directories whose config files apply, outermost first — the filesystem root down to
    /// <paramref name="startDir"/>.
    /// </summary>
    /// <remarks>
    /// Outermost first so that nearer files are merged later and therefore win, which is the same
    /// direction <c>.editorconfig</c> resolves in. Every ancestor is visited rather than stopping
    /// at the first file found: a repository root that sets the team's defaults and a subdirectory
    /// that overrides one of them is the arrangement this exists for.
    /// </remarks>
    private static IEnumerable<string> RepositoryDirectories(string startDir)
    {
        if (string.IsNullOrEmpty(startDir))
            yield break;

        DirectoryInfo? dir;
        try { dir = new DirectoryInfo(startDir); }
        catch { yield break; }

        var chain = new List<string>();
        while (dir is not null && dir.Exists)
        {
            chain.Add(dir.FullName);

            if (dir.Parent is null) break;
            if (string.Equals(dir.FullName, dir.Root.FullName, StringComparison.OrdinalIgnoreCase)) break;

            dir = dir.Parent;
        }

        for (int i = chain.Count - 1; i >= 0; i--)
            yield return chain[i];
    }

    private static LayeredConfig Merge(ImmutableArray<ConfigLayer> layers)
    {
        JsonObject? merged = null;
        string? primaryPath = null;
        string? loadError = null;

        foreach (var layer in layers)
        {
            if (layer.LoadError is { } error)
            {
                loadError ??= $"{layer.FilePath}: {error}";

                // A file that exists but does not parse is still the file someone is editing, so
                // it is the one worth naming — the caller's next line is usually about it.
                primaryPath = layer.FilePath;
                continue;
            }

            if (layer.Json is not { } json)
                continue;

            primaryPath = layer.FilePath;
            merged = merged is null ? (JsonObject)json.DeepClone() : DeepMerge(merged, json);
        }

        // Still names a path when nothing merged: the one file that exists is the broken one.
        if (merged is null)
            return new LayeredConfig(null, layers, primaryPath, loadError);

        try
        {
            var config = merged.Deserialize<RoslynSenseConfig>(s_options) ?? new RoslynSenseConfig();
            return new LayeredConfig(config, layers, primaryPath, loadError) { MergedJson = merged };
        }
        catch (JsonException ex)
        {
            // The individual files parsed as JSON but the merge does not bind — a string where a
            // number belongs, say. Named against the strongest file that exists, which is the one
            // most likely to have just been edited.
            return new LayeredConfig(null, layers, primaryPath, $"{primaryPath}: Invalid JSON: {ex.Message}")
            {
                MergedJson = merged,
            };
        }
        catch (Exception ex)
        {
            // Deliberately everything else. Binding runs this project's own converters, and a
            // converter does more than shape-check: resolving a connection string reads a file, so
            // it can fail with anything the filesystem raises. Every caller here is a host reading
            // its settings at start-up or re-reading them after an edit, and for both of those a
            // broken config file has to mean "run on defaults and say so" — an escaping exception
            // means no language server at all, which is the loudest possible way to report the
            // smallest possible problem.
            return new LayeredConfig(null, layers, primaryPath, $"{primaryPath}: {ex.Message}")
            {
                MergedJson = merged,
            };
        }
    }

    /// <summary>Writes <paramref name="overlay"/> over <paramref name="target"/>, in place.</summary>
    private static JsonObject DeepMerge(JsonObject target, JsonObject overlay)
    {
        foreach (var (key, value) in overlay)
        {
            if (value is JsonObject nested
                && target.TryGetPropertyValue(key, out var existing)
                && existing is JsonObject existingObject)
            {
                DeepMerge(existingObject, nested);
                continue;
            }

            target[key] = value?.DeepClone();
        }

        return target;
    }

    private static ConfigLayer ReadLayer(ConfigScope scope, string path)
    {
        if (!File.Exists(path))
            return new ConfigLayer(scope, path);

        try
        {
            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
                return new ConfigLayer(scope, path, new JsonObject());

            return JsonNode.Parse(json, documentOptions: s_documentOptions) is JsonObject parsed
                ? new ConfigLayer(scope, path, parsed)
                : new ConfigLayer(scope, path, LoadError: "Invalid JSON: expected an object.");
        }
        catch (JsonException ex)
        {
            return new ConfigLayer(scope, path, LoadError: $"Invalid JSON: {ex.Message}");
        }
        catch (IOException ex)
        {
            return new ConfigLayer(scope, path, LoadError: $"Read failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return new ConfigLayer(scope, path, LoadError: $"Access denied: {ex.Message}");
        }
    }
}

using System.Collections.Immutable;
using System.Text.Json;

namespace RoslynMCP.Languages.MsBuild.Core;

/// <summary>What MSBuild itself says a name means.</summary>
/// <param name="Description">One line, as documentation on a completion item or a hover.</param>
/// <param name="DefaultValues">The fixed set a property takes, when it takes one. Empty for the
/// ones that take a path, a version or arbitrary text.</param>
/// <param name="HelpLink">Upstream documentation, where there is a page for it.</param>
internal sealed record MsBuildHelpEntry(
    string Description,
    ImmutableArray<string> DefaultValues,
    string? HelpLink)
{
    public static MsBuildHelpEntry Empty { get; } = new(string.Empty, [], null);
}

/// <summary>
/// The vendored MSBuild documentation: properties and the values they take, item types and their
/// metadata, and elements and their attributes.
/// </summary>
/// <remarks>
/// <para>
/// Loaded once, lazily, from JSON embedded in the assembly. Embedded rather than copied beside the
/// binary because the server ships as a <c>PackAsTool</c> package: a file next to the DLL is a file
/// that has to be declared, published and found at runtime, and three of them is three chances to
/// ship a build where completion silently knows nothing.
/// </para>
/// <para>
/// Provenance and what is deliberately missing are in <c>Help/README.md</c> beside the JSON.
/// </para>
/// </remarks>
internal static class MsBuildSchemaHelp
{
    private static readonly Lazy<Corpus> s_corpus = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    private sealed record Corpus(
        ImmutableDictionary<string, MsBuildHelpEntry> Properties,
        ImmutableDictionary<string, MsBuildHelpEntry> Items,
        ImmutableDictionary<string, ImmutableDictionary<string, string>> ItemMetadata,
        ImmutableDictionary<string, MsBuildHelpEntry> Elements);

    /// <summary>Every property the corpus documents, for element-name completion under a
    /// <c>PropertyGroup</c>.</summary>
    public static IEnumerable<KeyValuePair<string, MsBuildHelpEntry>> Properties => s_corpus.Value.Properties;

    /// <summary>Every item type, for element-name completion under an <c>ItemGroup</c>.</summary>
    public static IEnumerable<KeyValuePair<string, MsBuildHelpEntry>> Items => s_corpus.Value.Items;

    public static MsBuildHelpEntry? Property(string name) =>
        s_corpus.Value.Properties.TryGetValue(name, out var entry) ? entry : null;

    public static MsBuildHelpEntry? Item(string name) =>
        s_corpus.Value.Items.TryGetValue(name, out var entry) ? entry : null;

    /// <summary>
    /// An element or attribute's documentation.
    /// </summary>
    /// <remarks>
    /// Attributes are keyed <c>Element.Attribute</c>, with <c>*.Condition</c> and <c>*.Label</c>
    /// standing for those two on any element — they are legal nearly everywhere, and the corpus
    /// says so once rather than per element.
    /// </remarks>
    public static MsBuildHelpEntry? Element(string name, string? attribute = null)
    {
        var elements = s_corpus.Value.Elements;

        if (attribute is null)
            return elements.TryGetValue(name, out var element) ? element : null;

        return elements.TryGetValue($"{name}.{attribute}", out var specific) ? specific
            : elements.TryGetValue($"*.{attribute}", out var wildcard) ? wildcard
            : null;
    }

    /// <summary>The metadata an item type carries, including the metadata every item type has.</summary>
    public static IReadOnlyDictionary<string, string> Metadata(string itemType)
    {
        var all = s_corpus.Value.ItemMetadata;
        var common = all.TryGetValue("*", out var shared) ? shared : ImmutableDictionary<string, string>.Empty;

        if (!all.TryGetValue(itemType, out var own))
            return common;

        var merged = common.ToBuilder();
        foreach (var (key, value) in own)
            merged[key] = value;

        return merged.ToImmutable();
    }

    private static Corpus Load()
    {
        var properties = Read("properties.json", out _);
        var items = Read("items.json", out var metadata);
        var elements = Read("elements.json", out _);

        return new Corpus(properties, items, metadata, elements);
    }

    private static ImmutableDictionary<string, MsBuildHelpEntry> Read(
        string fileName,
        out ImmutableDictionary<string, ImmutableDictionary<string, string>> metadata)
    {
        var entries = ImmutableDictionary.CreateBuilder<string, MsBuildHelpEntry>(StringComparer.OrdinalIgnoreCase);
        var metadataBuilder = ImmutableDictionary
            .CreateBuilder<string, ImmutableDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var assembly = typeof(MsBuildSchemaHelp).Assembly;
            string resource = $"RoslynMCP.Languages.MsBuild.Help.{fileName}";

            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                // A build that failed to embed the corpus. Completion falls back to the names the
                // pack knows itself rather than throwing on the first keystroke in a .csproj.
                Console.Error.WriteLine($"[MsBuild] Embedded help '{resource}' is missing.");
                metadata = metadataBuilder.ToImmutable();
                return entries.ToImmutable();
            }

            using var document = JsonDocument.Parse(stream);

            foreach (var property in document.RootElement.EnumerateObject())
            {
                entries[property.Name] = ReadEntry(property.Value);

                if (property.Value.TryGetProperty("metadata", out var metadataElement)
                    && metadataElement.ValueKind == JsonValueKind.Object)
                {
                    var own = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in metadataElement.EnumerateObject())
                        own[item.Name] = item.Value.GetString() ?? string.Empty;

                    metadataBuilder[property.Name] = own.ToImmutable();
                }
            }
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"[MsBuild] Embedded help '{fileName}' did not parse: {ex.Message}");
        }

        metadata = metadataBuilder.ToImmutable();
        return entries.ToImmutable();
    }

    private static MsBuildHelpEntry ReadEntry(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return MsBuildHelpEntry.Empty;

        string description = value.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
        string? helpLink = value.TryGetProperty("helpLink", out var h) ? h.GetString() : null;

        var defaults = ImmutableArray<string>.Empty;
        if (value.TryGetProperty("defaultValues", out var v) && v.ValueKind == JsonValueKind.Array)
        {
            var builder = ImmutableArray.CreateBuilder<string>();
            foreach (var item in v.EnumerateArray())
            {
                if (item.GetString() is { Length: > 0 } text)
                    builder.Add(text);
            }

            defaults = builder.ToImmutable();
        }

        return new MsBuildHelpEntry(description, defaults, helpLink);
    }
}

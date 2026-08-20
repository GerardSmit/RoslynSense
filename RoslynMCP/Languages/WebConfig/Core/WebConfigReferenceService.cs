using System.Collections.Immutable;
using RoslynMCP.Lsp;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Languages.WebConfig.Core;

/// <summary>A section and a name, compared the way the runtime's <c>NameValueCollection</c> lookup
/// is: the section exactly, the name without regard to case.</summary>
internal readonly record struct SettingKey(WebConfigSection Section, string Name)
{
    public static IEqualityComparer<SettingKey> Comparer { get; } = new CaseInsensitiveName();

    private sealed class CaseInsensitiveName : IEqualityComparer<SettingKey>
    {
        public bool Equals(SettingKey x, SettingKey y) =>
            x.Section == y.Section
            && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(SettingKey key) =>
            HashCode.Combine(key.Section, StringComparer.OrdinalIgnoreCase.GetHashCode(key.Name));
    }
}

/// <summary>
/// Where a <c>.config</c> setting is used: the C# that reads it by name and the markup that reads
/// it through an expression builder.
/// </summary>
/// <remarks>
/// No symbol side, unlike the appsettings pack's equivalent. A Framework app setting is a string
/// in a <c>NameValueCollection</c> — nothing binds it to a property whose own references would
/// count as the setting's.
/// </remarks>
internal static class WebConfigReferenceService
{
    public static LspLocation[] Usages(WebConfigView view, WebConfigEntry entry) =>
        Locations(
            view.Index.UsagesFor(entry.Section, entry.Name)
                .Concat(Markup(view.MarkupUsages, entry)));

    /// <summary>
    /// The same answer as <see cref="Usages"/>, for every name in the file at once.
    /// </summary>
    /// <remarks>
    /// <see cref="ConfigurationManagerUsageIndex.UsagesFor"/> is a scan of every usage in the
    /// project closure, so asking it once per <c>&lt;add&gt;</c> is quadratic in a file with
    /// hundreds of settings. Bucketing the same two sequences once costs one pass and answers all
    /// of them — which is what lets the CodeLens count be taken while the lenses are being drawn
    /// rather than one resolve at a time.
    /// </remarks>
    public static Dictionary<SettingKey, LspLocation[]> UsagesByName(WebConfigView view)
    {
        var pending = new Dictionary<SettingKey, List<ConfigSettingUsage>>(SettingKey.Comparer);

        // Index first and markup second, so a name read from both lists its C# sites in the same
        // order a single-entry lookup would.
        foreach (var usage in view.Index.Usages.Concat(view.MarkupUsages))
        {
            var key = new SettingKey(usage.Section, usage.Name);

            if (!pending.TryGetValue(key, out var list))
                pending[key] = list = [];

            list.Add(usage);
        }

        return pending.ToDictionary(kv => kv.Key, kv => Locations(kv.Value), SettingKey.Comparer);
    }

    /// <summary>One location per site, keeping the first spelling of a position read twice.</summary>
    private static LspLocation[] Locations(IEnumerable<ConfigSettingUsage> usages)
    {
        var locations = new List<LspLocation>();
        var seen = new HashSet<(string, int, int)>();

        foreach (var usage in usages)
        {
            var range = LspConverters.ToRange(usage.LineSpan);

            if (seen.Add((usage.FilePath, range.Start.Line, range.Start.Character)))
                locations.Add(new LspLocation(LspConverters.PathToUri(usage.FilePath), range));
        }

        return [.. locations];
    }

    public static IEnumerable<ConfigSettingUsage> Markup(
        ImmutableArray<ConfigSettingUsage> usages, WebConfigEntry entry) =>
        usages.Where(usage =>
            usage.Section == entry.Section
            && string.Equals(usage.Name, entry.Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Every file that declares this name, in override order — the application's own config first,
    /// a subdirectory's override after it. What "go to definition" from a read should land on, and
    /// what a peek from one declaration should show beside itself.
    /// </summary>
    public static LspLocation[] Declarations(
        string? projectFilePath, WebConfigSection section, string name)
    {
        if (projectFilePath is not { Length: > 0 })
            return [];

        var locations = new List<LspLocation>();

        foreach (string configFile in WebConfigSettings.ConfigFilesFor(projectFilePath))
        {
            if (WebConfigDocumentCache.Get(configFile) is not { } document
                || document.Find(section, name) is not { } entry)
            {
                continue;
            }

            locations.Add(Location(document, entry));
        }

        return [.. locations];
    }

    /// <summary>The entry's name attribute, or the head of its file when the reader declined to
    /// span it.</summary>
    public static LspLocation Location(WebConfigDocument document, WebConfigEntry entry) =>
        entry.NameSpan == default
            ? new LspLocation(
                LspConverters.PathToUri(document.FilePath),
                new Lsp.Protocol.Range(new Lsp.Protocol.Position(0, 0), new Lsp.Protocol.Position(0, 0)))
            : new LspLocation(
                LspConverters.PathToUri(document.FilePath),
                LspConverters.ToRange(document.Text.Lines, entry.NameSpan));
}

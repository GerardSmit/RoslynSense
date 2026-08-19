using System.Collections.Immutable;
using RoslynMCP.Lsp;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Languages.WebConfig.Core;

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
    public static LspLocation[] Usages(WebConfigView view, WebConfigEntry entry)
    {
        var locations = new List<LspLocation>();
        var seen = new HashSet<(string, int, int)>();

        void Add(ConfigSettingUsage usage)
        {
            var range = LspConverters.ToRange(usage.LineSpan);

            if (seen.Add((usage.FilePath, range.Start.Line, range.Start.Character)))
                locations.Add(new LspLocation(LspConverters.PathToUri(usage.FilePath), range));
        }

        foreach (var usage in view.Index.UsagesFor(entry.Section, entry.Name))
            Add(usage);

        foreach (var usage in Markup(view.MarkupUsages, entry))
            Add(usage);

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

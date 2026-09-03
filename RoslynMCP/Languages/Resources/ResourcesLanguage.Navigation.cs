using RoslynMCP.Languages.Resources.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Languages.Resources;

/// <summary>
/// F12 from a resource key: the neutral file first when it has the key, then the translations by
/// culture, then the customizations by rank — every file that answers, rather than the one the
/// runtime would have picked out of them.
/// </summary>
internal sealed partial class ResourcesLanguage : IEmbeddedDefinitionProvider
{
    /// <summary>
    /// <paramref name="typeDefinition"/> is ignored: a key has no type, so both questions have the
    /// same answer rather than one of them having none.
    /// </summary>
    public async Task<LspLocation[]> DefinitionAsync(
        EmbeddedStringContext context, bool typeDefinition, CancellationToken ct)
    {
        if (await KeyAtAsync(context, ct) is not { } match)
            return [];

        var locations = new List<LspLocation>();

        foreach (var file in Declaring(Loaded(match), match.Key))
        {
            if (KeyLocation(file, match.Key) is { } location)
                locations.Add(location);
        }

        return [.. locations];
    }

    /// <summary>The entry's <c>name=</c> attribute, or the top of the file when its span could not
    /// be pinned down exactly — landing in the right file beats not navigating.</summary>
    private static LspLocation? KeyLocation(ResourceFileIndex file, string key)
    {
        if (!file.Entries.TryGetValue(key, out var entry))
            return null;

        string uri = LspConverters.PathToUri(file.FilePath);

        if (entry.KeySpan.IsEmpty || ResourceCatalogService.Text(file.FilePath) is not { } text)
            return new LspLocation(uri, new Lsp.Protocol.Range(new Position(0, 0), new Position(0, 0)));

        return new LspLocation(uri, LspConverters.ToRange(text.Lines, entry.KeySpan));
    }
}

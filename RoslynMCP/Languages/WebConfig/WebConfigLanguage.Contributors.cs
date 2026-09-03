using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.WebConfig.Core;
using RoslynMCP.Lsp;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Languages.WebConfig;

internal sealed partial class WebConfigLanguage : ISymbolFreeReferenceProvider
{
    /// <summary>
    /// Find-references with the caret on a setting name in C# —
    /// <c>ConfigurationManager.AppSettings["Cdn|Root"]</c> — where there is no symbol to search
    /// for. Answers with the entry's declarations across the application's config files, every
    /// other C# site naming it, and the markup that reads it through an expression builder;
    /// answers null when the literal is not a setting name at all, which sends the request back
    /// down the ordinary road.
    /// </summary>
    public async Task<IReadOnlyList<LspLocation>?> ReferencesAsync(
        string filePath, int offset, Project? project, CancellationToken ct)
    {
        if (project is null || !filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return null;

        var index = await ConfigurationManagerUsageIndex.GetAsync(project, ct);

        if (index.At(filePath, offset) is not { } hit)
            return null;

        var locations = new List<LspLocation>();

        locations.AddRange(
            WebConfigReferenceService.Declarations(project.FilePath, hit.Section, hit.Name));

        foreach (var usage in index.UsagesFor(hit.Section, hit.Name))
        {
            locations.Add(new LspLocation(
                LspConverters.PathToUri(usage.FilePath), LspConverters.ToRange(usage.LineSpan)));
        }

        foreach (var usage in await MarkupSettingUsageIndex.ForProjectAsync(project, ct))
        {
            if (usage.Section == hit.Section
                && string.Equals(usage.Name, hit.Name, StringComparison.OrdinalIgnoreCase))
            {
                locations.Add(new LspLocation(
                    LspConverters.PathToUri(usage.FilePath), LspConverters.ToRange(usage.LineSpan)));
            }
        }

        return locations;
    }
}

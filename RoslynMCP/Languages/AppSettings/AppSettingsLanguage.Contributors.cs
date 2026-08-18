using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.AppSettings.Core;
using RoslynMCP.Lsp;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Languages.AppSettings;

internal sealed partial class AppSettingsLanguage : ISymbolFreeReferenceProvider
{
    /// <summary>
    /// Find-references with the caret on a configuration key literal in C# —
    /// <c>GetSection("Exam|ple")</c> — where there is no symbol to search for. Answers with the
    /// key's declarations across the project's configuration files and every other C# site
    /// naming the same path; answers null when the literal is not a configuration key at all,
    /// which sends the request back down the ordinary road.
    /// </summary>
    public async Task<IReadOnlyList<LspLocation>?> ReferencesAsync(
        string filePath, int offset, Project? project, CancellationToken ct)
    {
        if (project is null
            || !filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var index = await ConfigurationUsageIndex.GetAsync(project, ct);

        var hit = index.Usages.FirstOrDefault(usage =>
            string.Equals(usage.FilePath, filePath, StringComparison.OrdinalIgnoreCase)
            && usage.Span.Contains(offset));

        if (hit is null)
            return null;

        var locations = new List<LspLocation>();

        // The key's declarations: the same path in every configuration file feeding this
        // project, base file first, secrets last.
        if (project.FilePath is { Length: > 0 } projectPath)
        {
            foreach (string configFile in AppSettingsWorkspace.ConfigurationFilesFor(projectPath))
            {
                if (AppSettingsDocumentCache.Get(configFile) is { } document
                    && document.Find(hit.Path) is { } key)
                {
                    locations.Add(new LspLocation(
                        LspConverters.PathToUri(document.FilePath),
                        LspConverters.ToRange(document.Text.Lines, key.NameSpan)));
                }
            }
        }

        foreach (var usage in index.UsagesFor(hit.Path))
        {
            locations.Add(new LspLocation(
                LspConverters.PathToUri(usage.FilePath), LspConverters.ToRange(usage.LineSpan)));
        }

        return locations;
    }
}

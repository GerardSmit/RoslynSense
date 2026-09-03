using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMCP.Lsp;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Languages.AppSettings.Core;

/// <summary>
/// Where a configuration key is used from C#: the literal sites naming its path, the binding
/// sites covering its section, and — when the section is bound to an options type — every
/// reference to the property the key becomes.
/// </summary>
internal static class AppSettingsReferenceService
{
    public static async Task<LspLocation[]> UsagesAsync(
        AppSettingsView view, AppSettingsKey key, CancellationToken ct)
    {
        var locations = new List<LspLocation>();
        var seen = new HashSet<(string, int, int)>();

        void Add(string filePath, Lsp.Protocol.Range range)
        {
            if (seen.Add((filePath, range.Start.Line, range.Start.Character)))
                locations.Add(new LspLocation(LspConverters.PathToUri(filePath), range));
        }

        foreach (var usage in view.Index.UsagesFor(key.Path))
            Add(usage.FilePath, LspConverters.ToRange(usage.LineSpan));

        foreach (var binding in view.Index.BindingsFor(key.Path))
            Add(binding.FilePath, LspConverters.ToRange(binding.LineSpan));

        // A key under a bound section lives on as a property; the property's readers are the
        // key's readers. The declaration is left out — it is a mirror of the key, not a use.
        if (view.Project is { } project
            && view.Index.BoundProperty(key.Path) is { } property)
        {
            foreach (var reference in await SymbolFinder.FindReferencesAsync(
                property, project.Solution, ct))
            {
                foreach (var location in reference.Locations)
                {
                    if (!location.IsImplicit
                        && LspConverters.ToLocation(location.Location) is { } lsp)
                    {
                        Add(LspConverters.UriToPath(lsp.Uri), lsp.Range);
                    }
                }
            }
        }

        return [.. locations];
    }

    /// <summary>
    /// Every configuration file declaring a path, in probe order: the base file first, its
    /// environment overlays after it, the secrets store last.
    /// </summary>
    /// <remarks>
    /// The keyspace is one file split across several, so a key declared in more than one is
    /// declared more than once — and which of them wins depends on the environment the
    /// application runs under, which an editor cannot know. Answering with all of them lets the
    /// editor show the choice rather than guessing it.
    /// </remarks>
    public static LspLocation[] Declarations(string? projectFilePath, string path)
    {
        if (projectFilePath is not { Length: > 0 })
            return [];

        var locations = new List<LspLocation>();

        foreach (string configFile in AppSettingsWorkspace.ConfigurationFilesFor(projectFilePath))
        {
            if (AppSettingsDocumentCache.Get(configFile) is { } document
                && document.Find(path) is { } key)
            {
                locations.Add(new LspLocation(
                    LspConverters.PathToUri(document.FilePath),
                    LspConverters.ToRange(document.Text.Lines, key.NameSpan)));
            }
        }

        return [.. locations];
    }

    /// <summary>
    /// What "go to definition" on a key should mean: the property it binds to, when one exists —
    /// the one place its name is a symbol rather than a string.
    /// </summary>
    public static LspLocation? BoundPropertyLocation(AppSettingsView view, AppSettingsKey key)
    {
        if (view.Index.BoundProperty(key.Path) is not { } property)
            return null;

        foreach (var location in property.Locations)
        {
            if (LspConverters.ToLocation(location) is { } lsp)
                return lsp;
        }

        return null;
    }
}

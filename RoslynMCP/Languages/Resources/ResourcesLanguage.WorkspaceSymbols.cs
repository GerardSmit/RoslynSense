using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.PatternMatching;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Resources.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Languages.Resources;

internal sealed partial class ResourcesLanguage : ILanguageWorkspaceSymbolProvider
{
    /// <summary>Matches the cap the C# half applies, for the same reason: the client renders a
    /// picker, not a report.</summary>
    private const int MaxWorkspaceSymbols = 200;

    /// <summary>
    /// Resource keys, for Ctrl+T. A key is not a symbol and is in no compilation, so Roslyn's
    /// declaration search cannot see one.
    /// </summary>
    /// <remarks>
    /// The families come from the catalog rather than from a walk — the catalog is what the watcher
    /// and the file-system poller both keep fresh, and re-enumerating the solution's directories
    /// per keystroke in the picker is the difference between a usable feature and one that has to
    /// be switched off. Reading the key tables is unavoidable, because the eager half of the
    /// catalog knows names and not contents; it is paid once, since a materialized family is
    /// memoized against the files it was built from.
    /// </remarks>
    public async Task<IReadOnlyList<SymbolInformation>> WorkspaceSymbolsAsync(
        string query, Solution solution, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        // Roslyn's own matcher, so that "btnSub" and "bS" pick the same candidates among resource
        // keys that they pick in C# — a picker that ranked its halves by different rules would read
        // as a bug.
        using var matcher = PatternMatcher.CreatePatternMatcher(query, includeMatchedSpans: false);

        var results = new List<SymbolInformation>();
        var seenProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var texts = new Dictionary<string, SourceText?>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in solution.Projects)
        {
            ct.ThrowIfCancellationRequested();

            // A multi-targeted project appears once per framework over the same directory, and
            // every one of them would contribute the same files.
            if (project.FilePath is not { } path || !seenProjects.Add(path))
                continue;

            foreach (var family in (await CatalogAsync(project, ct)).Families)
            {
                ct.ThrowIfCancellationRequested();

                // Projects nested under one site root cover each other's directories.
                if (!seenFamilies.Add(Path.Combine(family.Directory, family.BaseName)))
                    continue;

                Collect(family, matcher, texts, results);

                if (results.Count >= MaxWorkspaceSymbols)
                    return results;
            }
        }

        return results;
    }

    private static void Collect(
        ResourceFamily family, PatternMatcher matcher,
        Dictionary<string, SourceText?> texts, List<SymbolInformation> results)
    {
        var loaded = ResourceCatalogService.Load(family);

        // The family's union, so a key its five translations also carry is one row rather than
        // five: the picker lists resources, not the files they happen to be written in.
        foreach (string key in loaded.AllKeys)
        {
            if (!matcher.Matches(key))
                continue;

            if (Declaration(loaded, key) is not { } declaration)
                continue;

            var (file, entry) = declaration;

            // The span was measured against whatever the key table was built from. A file edited
            // since then is dropped rather than pointed at, because the offset now means somewhere
            // else in it and the picker would land the user on the wrong line.
            if (TextOf(texts, file.FilePath) is not { } text || entry.KeySpan.End > text.Length)
                continue;

            results.Add(new SymbolInformation(
                key,
                LspSymbolKind.Key,
                new LspLocation(
                    LspConverters.PathToUri(file.FilePath),
                    LspConverters.ToRange(text.Lines, entry.KeySpan)),
                family.BaseName));

            if (results.Count >= MaxWorkspaceSymbols)
                return;
        }
    }

    /// <summary>
    /// Where the picker should land for a key: the family's own precedence order, so the neutral
    /// file wins whenever it has the key and a key that only a translation or a customization
    /// declares still resolves to the file that does declare it.
    /// </summary>
    private static (ResourceFileIndex File, ResourceEntry Entry)? Declaration(
        ResourceFamily family, string key)
    {
        foreach (var file in family.Files)
        {
            if (file.Entries.TryGetValue(key, out var entry) && entry.KeySpan != default)
                return (file, entry);
        }

        return null;
    }

    /// <summary>
    /// The file's text, read at most once per request. Spans are offsets and a client wants lines,
    /// and several keys in one file would otherwise each re-read it.
    /// </summary>
    private static SourceText? TextOf(Dictionary<string, SourceText?> texts, string filePath)
    {
        if (!texts.TryGetValue(filePath, out var text))
            texts[filePath] = text = ResourceCatalogService.Text(filePath);

        return text;
    }
}

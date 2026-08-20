using Microsoft.CodeAnalysis;
using RoslynMCP.Languages;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Lsp.Search;
using RoslynMCP.Services;
using RoslynMCP.Services.ExternalSource;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>roslynSense/searchEverywhere: the ranked Ctrl+T list the extension renders itself.</summary>
internal static class SearchEverywhereHandler
{
    private const int MaxResults = 200;

    public static async Task<SearchEverywhereResult> SearchAsync(
        SearchEverywhereParams p, CancellationToken ct, LanguageSession? languages = null)
    {
        // A search that ran while the solution was still loading used to answer out of whatever
        // subset happened to be loaded, which reads as "Ctrl+T does not find my type" rather than
        // as "not yet". Waiting is cancelled along with the request, so a query the user has
        // already retyped past stops waiting with it.
        await SolutionWarmup.WaitAsync(ct);

        var solution = WorkspaceService.TryGetMostRecentSolution();
        if (solution is null)
            return new SearchEverywhereResult([], false);

        int limit = p.MaxResults is > 0 and <= MaxResults ? p.MaxResults : 50;

        SearchItemKind? only = p.Only?.ToLowerInvariant() switch
        {
            "type" => SearchItemKind.Type,
            "member" => SearchItemKind.Member,
            "file" => SearchItemKind.File,
            _ => null,
        };

        // One extra result is asked for so the client can say "there are more" without the server
        // having to count everything it threw away.
        var hits = await ClaimedAsync(p.Query, solution, only, languages, ct)
            ?? await SearchEverywhere.SearchAsync(
                solution, p.Query, limit + 1, ct,
                only: only, includeMetadata: p.IncludeMetadata);

        bool truncated = hits.Count > limit;
        var items = hits
            .Take(limit)
            .Select(hit => new SearchEverywhereItem(
                hit.Kind.ToString().ToLowerInvariant(),
                hit.Name,
                hit.Container,
                hit.Uri ?? LspConverters.PathToUri(hit.FilePath),
                hit.FilePath,
                hit.Line,
                hit.Character,
                hit.SymbolKind))
            .ToArray();

        return new SearchEverywhereResult(items, truncated);
    }

    /// <summary>
    /// The packs' answer when one of them recognises the query as its own, or null when none does.
    /// </summary>
    /// <remarks>
    /// Replaces the ordinary search rather than joining it, which is the contract on
    /// <see cref="ILanguageSearchContributor"/>: a query a pack claims — a pasted runtime control
    /// id — means nothing to the generic matcher, so what it would contribute underneath is a list
    /// of typo-corrected guesses with the real answer buried in it.
    /// <para>
    /// The kind filter is applied here rather than in the packs, so that a pack answers what it
    /// knows and the tab decides what it wants. A claim that survives the filter with nothing left
    /// is no claim: the Classes tab falls through to the ordinary search rather than going blank.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyList<SearchHit>?> ClaimedAsync(
        string query, Solution solution, SearchItemKind? only,
        LanguageSession? languages, CancellationToken ct)
    {
        List<SearchHit>? claimed = null;

        foreach (var contributor in
                 LanguageScope.Of(languages).Contributors<ILanguageSearchContributor>())
        {
            var hits = await contributor.SearchAsync(query, solution, ct);
            if (hits.Count == 0)
                continue;

            (claimed ??= []).AddRange(hits);
        }

        if (claimed is null)
            return null;

        var kept = only is { } kind
            ? claimed.Where(hit => hit.Kind == kind).ToList()
            : claimed;

        if (kept.Count == 0)
            return null;

        kept.Sort((a, b) => a.Score.CompareTo(b.Score));
        return kept;
    }

    public static async Task<ResolveMetadataResult?> ResolveMetadataAsync(
        ResolveMetadataParams p, CancellationToken ct)
    {
        if (!VirtualDocumentHandler.TryParseUri(p.Uri, out string scheme, out string assemblyPath, out string typeName)
            || scheme != VirtualDocumentHandler.MetadataScheme)
            return null;

        var resolved = await ExternalSourceService.TryResolveTypeAsync(assemblyPath, typeName, ct);
        if (resolved is null)
            return null;

        return new ResolveMetadataResult(
            LspConverters.PathToUri(resolved.FilePath),
            resolved.FilePath,
            resolved.Primary.Line,
            resolved.Primary.Character);
    }

    private const int MaxTextResults = 500;

    public static async Task<SearchTextResult> SearchTextAsync(SearchTextParams p, CancellationToken ct)
    {
        await SolutionWarmup.WaitAsync(ct);

        var solution = WorkspaceService.TryGetMostRecentSolution();
        if (solution is null)
            return new SearchTextResult([], false);

        int limit = p.MaxResults is > 0 and <= MaxTextResults ? p.MaxResults : 100;
        var (hits, truncated) = await TextSearch.SearchAsync(solution, p.Query, limit, ct);

        var items = hits
            .Select(hit => new SearchTextItem(
                LspConverters.PathToUri(hit.FilePath),
                hit.FilePath,
                hit.Line,
                hit.Character,
                hit.LineText))
            .ToArray();

        return new SearchTextResult(items, truncated);
    }
}

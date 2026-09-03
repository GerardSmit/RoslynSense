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
        SearchItemKind? only = p.Only?.ToLowerInvariant() switch
        {
            "type" => SearchItemKind.Type,
            "member" => SearchItemKind.Member,
            "file" => SearchItemKind.File,
            _ => null,
        };

        var timer = SearchTimer.Start("Search Everywhere", p.Query, only switch
        {
            SearchItemKind.Type => "types only",
            SearchItemKind.Member => "members only",
            SearchItemKind.File => "files only",
            _ => p.IncludeMetadata ? "with non-solution items" : null,
        });

        // Somebody is waiting on this: the background index sweep gives way for as long as it runs.
        using var busy = ForegroundGate.Busy();

        int limit = p.MaxResults is > 0 and <= MaxResults ? p.MaxResults : 50;

        // The solution is being evaluated for the first time: answer from the names read off disk
        // instead of waiting for MSBuild. This is the cold-open case, and the whole of what used to
        // make the first Ctrl+T of a session cost the better part of ten seconds — the matching
        // underneath was always milliseconds. Only the first load, and only if the index wins the
        // race; otherwise nothing here happened at all and the search below is the one that runs.
        if (!SolutionWarmup.HasLoadedOnce
            && await NameIndex.ReadyBeforeAsync(SolutionWarmup.Loading, ct) is { } names)
        {
            timer.CorpusReady();
            var provisional = SearchEverywhere.SearchNames(names, p.Query, limit + 1, ct, only: only);
            timer.Done(provisional.Count, "name index");
            return Result(provisional, limit, loading: true);
        }

        // A search that ran while the solution was still loading used to answer out of whatever
        // subset happened to be loaded, which reads as "Ctrl+T does not find my type" rather than
        // as "not yet". Waiting is cancelled along with the request, so a query the user has
        // already retyped past stops waiting with it.
        await SolutionWarmup.WaitAsync(ct);

        var solution = WorkspaceService.TryGetSessionSolution();
        if (solution is null)
            return new SearchEverywhereResult([], false, false);

        timer.CorpusReady();

        // One extra result is asked for so the client can say "there are more" without the server
        // having to count everything it threw away.
        var hits = await ClaimedAsync(p.Query, solution, only, languages, ct)
            ?? await SearchEverywhere.SearchAsync(
                solution, p.Query, limit + 1, ct,
                only: only, includeMetadata: p.IncludeMetadata);

        timer.Done(hits.Count, "solution");
        return Result(hits, limit, loading: false);
    }

    /// <param name="loading">Whether this answer came from the stand-in corpus, so the client can
    /// say so and ask again once <c>roslynSense/solutionReady</c> arrives.</param>
    private static SearchEverywhereResult Result(
        IReadOnlyList<SearchHit> hits, int limit, bool loading)
    {
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

        return new SearchEverywhereResult(items, hits.Count > limit, loading);
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
        var timer = SearchTimer.Start("Text search", p.Query);
        using var busy = ForegroundGate.Busy();

        int limit = p.MaxResults is > 0 and <= MaxTextResults ? p.MaxResults : 100;

        bool loading = false;
        IReadOnlyList<TextHit> hits;
        bool truncated;

        // The Text tab scans files, so the only thing it ever needed from the solution was the
        // list of them — and the name index walked exactly that list before the load started. It
        // is the one tab whose provisional answer is not provisional at all; it is marked as one
        // anyway, because the walk behind it predates any project the load might still add.
        if (!SolutionWarmup.HasLoadedOnce
            && await NameIndex.ReadyBeforeAsync(SolutionWarmup.Loading, ct) is { } names)
        {
            loading = true;
            timer.CorpusReady();
            (hits, truncated) = await TextSearch.SearchAsync(names.Files, p.Query, limit, ct);
        }
        else
        {
            await SolutionWarmup.WaitAsync(ct);

            var solution = WorkspaceService.TryGetSessionSolution();
            if (solution is null)
                return new SearchTextResult([], false, false);

            timer.CorpusReady();
            (hits, truncated) = await TextSearch.SearchAsync(solution, p.Query, limit, ct);
        }

        timer.Done(hits.Count, loading ? "name index" : "solution");

        var items = hits
            .Select(hit => new SearchTextItem(
                LspConverters.PathToUri(hit.FilePath),
                hit.FilePath,
                hit.Line,
                hit.Character,
                hit.LineText))
            .ToArray();

        return new SearchTextResult(items, truncated, loading);
    }
}

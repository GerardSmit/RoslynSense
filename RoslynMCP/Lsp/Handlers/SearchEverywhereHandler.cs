using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Lsp.Search;
using RoslynMCP.Services;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>roslynSense/searchEverywhere: the ranked Ctrl+T list the extension renders itself.</summary>
internal static class SearchEverywhereHandler
{
    private const int MaxResults = 200;

    public static async Task<SearchEverywhereResult> SearchAsync(
        SearchEverywhereParams p, CancellationToken ct)
    {
        var solution = WorkspaceService.TryGetMostRecentSolution();
        if (solution is null)
            return new SearchEverywhereResult([], false);

        int limit = p.MaxResults is > 0 and <= MaxResults ? p.MaxResults : 50;

        // One extra result is asked for so the client can say "there are more" without the server
        // having to count everything it threw away.
        var hits = await SearchEverywhere.SearchAsync(solution, p.Query, limit + 1, ct);

        bool truncated = hits.Count > limit;
        var items = hits
            .Take(limit)
            .Select(hit => new SearchEverywhereItem(
                hit.Kind.ToString().ToLowerInvariant(),
                hit.Name,
                hit.Container,
                LspConverters.PathToUri(hit.FilePath),
                hit.FilePath,
                hit.Line,
                hit.Character,
                hit.SymbolKind))
            .ToArray();

        return new SearchEverywhereResult(items, truncated);
    }
}

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
        SearchEverywhereParams p, CancellationToken ct)
    {
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
        var hits = await SearchEverywhere.SearchAsync(
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

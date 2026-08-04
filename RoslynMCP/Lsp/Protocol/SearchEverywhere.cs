using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

/// <summary>
/// roslynSense/searchEverywhere — one query over types, members and files, ranked by the server.
/// A custom method rather than <c>workspace/symbol</c> because the client renders the result list
/// itself: the built-in symbol picker re-sorts, drops files, and has nowhere to put a kind filter.
/// </summary>
public sealed record SearchEverywhereParams(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("maxResults")] int MaxResults = 50);

public sealed record SearchEverywhereItem(
    [property: JsonPropertyName("kind")] string Kind, // "type" | "member" | "file"
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("container")] string? Container,
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("character")] int Character,
    [property: JsonPropertyName("symbolKind")] int SymbolKind);

public sealed record SearchEverywhereResult(
    [property: JsonPropertyName("items")] SearchEverywhereItem[] Items,
    [property: JsonPropertyName("truncated")] bool Truncated);

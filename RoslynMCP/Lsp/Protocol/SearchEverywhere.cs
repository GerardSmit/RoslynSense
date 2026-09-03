using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

/// <summary>
/// roslynSense/searchEverywhere — one query over types, members and files, ranked by the server.
/// A custom method rather than <c>workspace/symbol</c> because the client renders the result list
/// itself: the built-in symbol picker re-sorts, drops files, and has nowhere to put a kind filter.
/// </summary>
/// <param name="Only">Restricts to one kind: "type", "member" or "file" — the panel's tabs.</param>
/// <param name="IncludeMetadata">Also searches public types of referenced assemblies; those hits
/// carry a <c>roslynsense-metadata:</c> URI and open as decompiled source.</param>
public sealed record SearchEverywhereParams(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("maxResults")] int MaxResults = 50,
    [property: JsonPropertyName("only")] string? Only = null,
    [property: JsonPropertyName("includeMetadata")] bool IncludeMetadata = false);

public sealed record SearchEverywhereItem(
    [property: JsonPropertyName("kind")] string Kind, // "type" | "member" | "file"
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("container")] string? Container,
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("character")] int Character,
    [property: JsonPropertyName("symbolKind")] int SymbolKind);

/// <param name="Loading">The bound solution was still being evaluated, so these rows came from the
/// names read off disk rather than from the workspace. The client says so and asks again when
/// <c>roslynSense/solutionReady</c> arrives — see <c>Lsp.Search.NameIndex</c>.</param>
public sealed record SearchEverywhereResult(
    [property: JsonPropertyName("items")] SearchEverywhereItem[] Items,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("loading")] bool Loading = false);

/// <summary>
/// roslynSense/resolveMetadataTarget — turns a <c>roslynsense-metadata:</c> search hit into the
/// decompiled file on disk plus the type's position, the same target F12 lands on. Resolved on
/// open rather than at search time: decompiling every hit in a result list would cost seconds
/// for a list the user will open one row of.
/// </summary>
public sealed record ResolveMetadataParams(
    [property: JsonPropertyName("uri")] string Uri);

public sealed record ResolveMetadataResult(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("character")] int Character);

/// <summary>
/// roslynSense/searchText — the Text tab: a literal, case-insensitive scan over every file the
/// solution's directory walk knows about.
/// </summary>
public sealed record SearchTextParams(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("maxResults")] int MaxResults = 100);

public sealed record SearchTextItem(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("character")] int Character,
    [property: JsonPropertyName("lineText")] string LineText);

public sealed record SearchTextResult(
    [property: JsonPropertyName("items")] SearchTextItem[] Items,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("loading")] bool Loading = false);

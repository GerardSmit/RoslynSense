using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

// Call and type hierarchy (LSP 3.16/3.17). Items carry no opaque data: the symbol is
// re-resolved from Uri + SelectionRange.Start on each incoming/outgoing/super/sub request,
// so items stay valid across edits as long as the identifier is still there.

public sealed record HierarchyItem(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] int Kind,
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("range")] Range Range,
    [property: JsonPropertyName("selectionRange")] Range SelectionRange,
    [property: JsonPropertyName("detail")] string? Detail);

public sealed record CallHierarchyCallsParams(
    [property: JsonPropertyName("item")] HierarchyItem Item);

public sealed record CallHierarchyIncomingCall(
    [property: JsonPropertyName("from")] HierarchyItem From,
    [property: JsonPropertyName("fromRanges")] Range[] FromRanges);

public sealed record CallHierarchyOutgoingCall(
    [property: JsonPropertyName("to")] HierarchyItem To,
    [property: JsonPropertyName("fromRanges")] Range[] FromRanges);

public sealed record TypeHierarchyItemParams(
    [property: JsonPropertyName("item")] HierarchyItem Item);

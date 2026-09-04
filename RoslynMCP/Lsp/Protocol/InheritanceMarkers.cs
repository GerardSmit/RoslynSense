using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

// Custom extension (roslynSense/inheritanceMarkers): Rider/VS-style gutter arrows.
// "Up" kinds (base, implements, overrides) point at what a declaration inherits from;
// "down" kinds (derived, implemented, overridden) point at what inherits from it.

public sealed record InheritanceMarkersParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument);

public sealed record InheritanceMarker(
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("character")] int Character, // declaration identifier start
    [property: JsonPropertyName("kind")] string Kind, // base|implements|overrides|derived|implemented|overridden
    [property: JsonPropertyName("targets")] InheritanceTarget[] Targets);

/// <summary>Uri is null for metadata symbols (framework interfaces, base classes from
/// packages) — the client then asks roslynSense/resolveInheritanceTarget, which decompiles
/// the containing type on demand and returns a real location.</summary>
public sealed record InheritanceTarget(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("uri")] string? Uri,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("character")] int Character);

/// <summary>roslynSense/inheritanceAt: the markers for the one declaration around a position —
/// the member whose identifier a lens sits above, or the one the cursor is inside.</summary>
public sealed record InheritanceAtParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("character")] int Character);

public sealed record ResolveInheritanceTargetParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("character")] int Character,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("index")] int Index);

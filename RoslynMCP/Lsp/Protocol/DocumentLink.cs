using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

public sealed record DocumentLinkParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument);

/// <summary>
/// A span the editor turns into a clickable link. <see cref="Target"/> is a URI; leaving it null
/// marks the link as needing a documentLink/resolve round-trip, which is worth doing only when
/// working out the target costs more than producing the range did.
/// </summary>
public sealed record DocumentLink(
    [property: JsonPropertyName("range")] Range Range,
    [property: JsonPropertyName("target")] string? Target = null,
    [property: JsonPropertyName("tooltip")] string? Tooltip = null);

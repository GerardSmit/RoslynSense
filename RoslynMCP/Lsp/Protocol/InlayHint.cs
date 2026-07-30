using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

public sealed record InlayHintParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("range")] Range Range);

public sealed record InlayHint(
    [property: JsonPropertyName("position")] Position Position,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("kind")] int? Kind, // 1 type, 2 parameter
    [property: JsonPropertyName("paddingLeft")] bool PaddingLeft,
    [property: JsonPropertyName("paddingRight")] bool PaddingRight);

public sealed record InlayHintOptions(
    [property: JsonPropertyName("resolveProvider")] bool ResolveProvider);

using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

public sealed record SignatureHelpParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("position")] Position Position,
    [property: JsonPropertyName("context")] SignatureHelpContext? Context = null);

public sealed record SignatureHelpContext(
    [property: JsonPropertyName("triggerKind")] int TriggerKind, // 1 invoked, 2 trigger char, 3 content change
    [property: JsonPropertyName("triggerCharacter")] string? TriggerCharacter,
    [property: JsonPropertyName("isRetrigger")] bool IsRetrigger);

public sealed record SignatureHelp(
    [property: JsonPropertyName("signatures")] SignatureInformation[] Signatures,
    [property: JsonPropertyName("activeSignature")] int ActiveSignature,
    [property: JsonPropertyName("activeParameter")] int ActiveParameter);

public sealed record SignatureInformation(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("documentation")] MarkupContent? Documentation,
    [property: JsonPropertyName("parameters")] ParameterInformation[] Parameters);

public sealed record ParameterInformation(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("documentation")] MarkupContent? Documentation);

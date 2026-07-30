using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

public sealed record SemanticTokensParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument);

public sealed record SemanticTokens(
    [property: JsonPropertyName("data")] int[] Data);

public sealed record SemanticTokensOptions(
    [property: JsonPropertyName("legend")] SemanticTokensLegend Legend,
    [property: JsonPropertyName("full")] bool Full);

public sealed record SemanticTokensLegend(
    [property: JsonPropertyName("tokenTypes")] string[] TokenTypes,
    [property: JsonPropertyName("tokenModifiers")] string[] TokenModifiers);

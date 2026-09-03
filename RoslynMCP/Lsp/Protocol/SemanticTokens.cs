using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

public sealed record SemanticTokensParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument);

public sealed record SemanticTokensRangeParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("range")] Range Range);

public sealed record SemanticTokensDeltaParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("previousResultId")] string PreviousResultId);

/// <summary>
/// A full token set. <see cref="ResultId"/> is what the client sends back as
/// <c>previousResultId</c> on its next request, letting the server answer with edits instead of
/// the whole file.
/// </summary>
public sealed record SemanticTokens(
    [property: JsonPropertyName("data")] int[] Data,
    [property: JsonPropertyName("resultId")] string? ResultId = null);

/// <summary>Edits against the token array the client already holds.</summary>
public sealed record SemanticTokensDelta(
    [property: JsonPropertyName("resultId")] string? ResultId,
    [property: JsonPropertyName("edits")] SemanticTokensEdit[] Edits);

public sealed record SemanticTokensEdit(
    [property: JsonPropertyName("start")] int Start,
    [property: JsonPropertyName("deleteCount")] int DeleteCount,
    [property: JsonPropertyName("data")] int[]? Data);

public sealed record SemanticTokensOptions(
    [property: JsonPropertyName("legend")] SemanticTokensLegend Legend,
    [property: JsonPropertyName("full")] SemanticTokensFullOptions Full,
    [property: JsonPropertyName("range")] bool Range);

/// <summary>The object form of <c>full</c>, which is the only way to advertise delta support.</summary>
public sealed record SemanticTokensFullOptions(
    [property: JsonPropertyName("delta")] bool Delta);

public sealed record SemanticTokensLegend(
    [property: JsonPropertyName("tokenTypes")] string[] TokenTypes,
    [property: JsonPropertyName("tokenModifiers")] string[] TokenModifiers);

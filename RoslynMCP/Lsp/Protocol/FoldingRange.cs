using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

public sealed record FoldingRangeParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument);

public sealed record FoldingRange(
    [property: JsonPropertyName("startLine")] int StartLine,
    [property: JsonPropertyName("endLine")] int EndLine,
    [property: JsonPropertyName("kind")] string? Kind); // "comment" | "imports" | "region" | null

public static class FoldingRangeKind
{
    public const string Comment = "comment";
    public const string Imports = "imports";
    public const string Region = "region";
}

public sealed record DocumentRangeFormattingParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("range")] Range Range);

public sealed record DocumentOnTypeFormattingParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("position")] Position Position,
    [property: JsonPropertyName("ch")] string Character);

public sealed record DocumentOnTypeFormattingOptions(
    [property: JsonPropertyName("firstTriggerCharacter")] string FirstTriggerCharacter,
    [property: JsonPropertyName("moreTriggerCharacter")] string[] MoreTriggerCharacter);

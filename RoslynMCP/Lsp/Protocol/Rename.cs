using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

public sealed record RenameParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("position")] Position Position,
    [property: JsonPropertyName("newName")] string NewName);

/// <summary>prepareRename result: the range of the symbol plus its current text.</summary>
public sealed record PrepareRenameResult(
    [property: JsonPropertyName("range")] Range Range,
    [property: JsonPropertyName("placeholder")] string Placeholder);

using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

// Custom extension (roslynSense/onAutoInsert): the client calls this after the user types
// "///" and applies the returned edit, then moves the caret to Cursor. Modeled after
// Roslyn's textDocument/_vs_onAutoInsert, minus VS snippet syntax.

public sealed record OnAutoInsertParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("position")] Position Position);

public sealed record OnAutoInsertResult(
    [property: JsonPropertyName("edit")] TextEdit Edit,
    [property: JsonPropertyName("cursor")] Position Cursor);

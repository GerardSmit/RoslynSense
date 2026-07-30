using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

// Hand-rolled LSP 3.17 contract types (System.Text.Json). We own these instead of taking a
// dependency: OmniSharp's LSP library is unmaintained and Newtonsoft-based, and Microsoft's
// protocol packages are VS-internal/prerelease. Only the subset the server implements exists.

public sealed record Position(
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("character")] int Character);

public sealed record Range(
    [property: JsonPropertyName("start")] Position Start,
    [property: JsonPropertyName("end")] Position End);

public sealed record Location(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("range")] Range Range);

public sealed record TextDocumentIdentifier(
    [property: JsonPropertyName("uri")] string Uri);

public sealed record VersionedTextDocumentIdentifier(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("version")] int Version);

public sealed record TextDocumentItem(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("languageId")] string LanguageId,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("text")] string Text);

public sealed record TextDocumentPositionParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("position")] Position Position);

public sealed record TextEdit(
    [property: JsonPropertyName("range")] Range Range,
    [property: JsonPropertyName("newText")] string NewText);

/// <summary>Edits keyed by document URI. The simple (non-documentChanges) form — every LSP
/// client supports it and it needs no version bookkeeping.</summary>
public sealed record WorkspaceEdit(
    [property: JsonPropertyName("changes")] Dictionary<string, TextEdit[]> Changes);

public sealed record MarkupContent(
    [property: JsonPropertyName("kind")] string Kind, // "plaintext" | "markdown"
    [property: JsonPropertyName("value")] string Value);

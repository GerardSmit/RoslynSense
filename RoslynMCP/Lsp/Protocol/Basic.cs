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

/// <summary>
/// Edits keyed by document URI, plus the ordered form when the edit also has to move files.
/// </summary>
/// <remarks>
/// <see cref="Changes"/> is the simple form: every LSP client supports it and it needs no version
/// bookkeeping, so it stays the default and every caller that only rewrites text keeps using it
/// alone. <see cref="DocumentChanges"/> is the only form that can carry a resource operation —
/// renaming <c>Default.aspx</c> has to take its code-behind and designer with it — and the
/// protocol says a client that understands it ignores <see cref="Changes"/> entirely. So when
/// there are operations to send, the text edits are repeated into both: the duplication costs a
/// modern client nothing and leaves the edits intact for one that only reads <c>changes</c>.
/// </remarks>
public sealed record WorkspaceEdit(
    [property: JsonPropertyName("changes")] Dictionary<string, TextEdit[]> Changes,
    [property: JsonPropertyName("documentChanges")] object[]? DocumentChanges = null);

/// <summary>One document's edits inside <see cref="WorkspaceEdit.DocumentChanges"/>.</summary>
public sealed record TextDocumentEdit(
    [property: JsonPropertyName("textDocument")] OptionalVersionedTextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("edits")] TextEdit[] Edits);

/// <summary>
/// A document identifier whose version may be <c>null</c>, meaning "apply whatever the buffer is
/// at now". The server does not track client document versions, so it always is.
/// </summary>
/// <remarks>
/// The version is written even when null, against the connection's
/// <c>WhenWritingNull</c> default: the protocol declares the field as <c>integer | null</c>, and
/// a client reading it as absent rather than null is a difference this server should not create.
/// </remarks>
public sealed record OptionalVersionedTextDocumentIdentifier(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("version")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Version = null);

/// <summary>
/// A file move inside <see cref="WorkspaceEdit.DocumentChanges"/>, so the client performs it in
/// the same undo step as the edits around it.
/// </summary>
public sealed record RenameFile(
    [property: JsonPropertyName("oldUri")] string OldUri,
    [property: JsonPropertyName("newUri")] string NewUri)
{
    [JsonPropertyName("kind")]
    public string Kind => "rename";
}

public sealed record MarkupContent(
    [property: JsonPropertyName("kind")] string Kind, // "plaintext" | "markdown"
    [property: JsonPropertyName("value")] string Value);

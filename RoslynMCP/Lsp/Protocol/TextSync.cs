using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

public sealed record DidOpenTextDocumentParams(
    [property: JsonPropertyName("textDocument")] TextDocumentItem TextDocument);

public sealed record DidChangeTextDocumentParams(
    [property: JsonPropertyName("textDocument")] VersionedTextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("contentChanges")] TextDocumentContentChangeEvent[] ContentChanges);

/// <summary>Incremental when <see cref="Range"/> is present; full-document replace otherwise.</summary>
public sealed record TextDocumentContentChangeEvent(
    [property: JsonPropertyName("range")] Range? Range,
    [property: JsonPropertyName("text")] string Text);

public sealed record DidCloseTextDocumentParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument);

public sealed record DidSaveTextDocumentParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("text")] string? Text);

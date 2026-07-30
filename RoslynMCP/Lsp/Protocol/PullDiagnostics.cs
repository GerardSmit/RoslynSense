using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

// LSP 3.17 pull diagnostics with resultId round-tripping: when the document text and its
// project's dependent-semantic version are unchanged since the client's previousResultId,
// the server answers "unchanged" without recomputing.

public sealed record DocumentDiagnosticParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("previousResultId")] string? PreviousResultId = null);

public sealed record FullDocumentDiagnosticReport(
    [property: JsonPropertyName("kind")] string Kind, // always "full"
    [property: JsonPropertyName("items")] Diagnostic[] Items)
{
    [JsonPropertyName("resultId")] public string? ResultId { get; init; }
}

public sealed record UnchangedDocumentDiagnosticReport(
    [property: JsonPropertyName("kind")] string Kind, // always "unchanged"
    [property: JsonPropertyName("resultId")] string ResultId);

public sealed record DiagnosticOptions(
    [property: JsonPropertyName("interFileDependencies")] bool InterFileDependencies,
    [property: JsonPropertyName("workspaceDiagnostics")] bool WorkspaceDiagnostics);

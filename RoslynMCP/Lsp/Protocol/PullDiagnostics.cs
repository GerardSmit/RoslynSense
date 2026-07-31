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

// Workspace-wide pull: the Problems panel without having to open every file first.

public sealed record WorkspaceDiagnosticParams(
    [property: JsonPropertyName("previousResultIds")] PreviousResultId[]? PreviousResultIds = null);

public sealed record PreviousResultId(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("value")] string Value);

public sealed record WorkspaceDiagnosticReport(
    [property: JsonPropertyName("items")] object[] Items);

public sealed record WorkspaceFullDocumentDiagnosticReport(
    [property: JsonPropertyName("kind")] string Kind, // always "full"
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("items")] Diagnostic[] Items)
{
    [JsonPropertyName("resultId")] public string? ResultId { get; init; }
    [JsonPropertyName("version")] public int? Version { get; init; }
}

public sealed record WorkspaceUnchangedDocumentDiagnosticReport(
    [property: JsonPropertyName("kind")] string Kind, // always "unchanged"
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("resultId")] string ResultId);

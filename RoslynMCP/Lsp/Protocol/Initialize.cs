using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

/// <summary>Initialize params — only the fields the server reads. Unknown fields are ignored
/// by deserialization, which is all LSP forward-compatibility requires.</summary>
public sealed record InitializeParams(
    [property: JsonPropertyName("processId")] int? ProcessId,
    [property: JsonPropertyName("rootUri")] string? RootUri,
    [property: JsonPropertyName("workspaceFolders")] WorkspaceFolder[]? WorkspaceFolders);

public sealed record WorkspaceFolder(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("name")] string Name);

public sealed record InitializeResult(
    [property: JsonPropertyName("capabilities")] ServerCapabilities Capabilities,
    [property: JsonPropertyName("serverInfo")] ServerInfo ServerInfo);

public sealed record ServerInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string? Version);

public sealed record ServerCapabilities
{
    [JsonPropertyName("positionEncoding")] public string PositionEncoding { get; init; } = "utf-16";
    [JsonPropertyName("textDocumentSync")] public TextDocumentSyncOptions? TextDocumentSync { get; init; }
    [JsonPropertyName("definitionProvider")] public bool DefinitionProvider { get; init; }
    [JsonPropertyName("typeDefinitionProvider")] public bool TypeDefinitionProvider { get; init; }
    [JsonPropertyName("referencesProvider")] public bool ReferencesProvider { get; init; }
    [JsonPropertyName("implementationProvider")] public bool ImplementationProvider { get; init; }
    [JsonPropertyName("hoverProvider")] public bool HoverProvider { get; init; }
    [JsonPropertyName("documentSymbolProvider")] public bool DocumentSymbolProvider { get; init; }
    [JsonPropertyName("workspaceSymbolProvider")] public bool WorkspaceSymbolProvider { get; init; }
    [JsonPropertyName("documentHighlightProvider")] public bool DocumentHighlightProvider { get; init; }
    [JsonPropertyName("renameProvider")] public RenameOptions? RenameProvider { get; init; }
    [JsonPropertyName("completionProvider")] public CompletionOptions? CompletionProvider { get; init; }
    [JsonPropertyName("signatureHelpProvider")] public SignatureHelpOptions? SignatureHelpProvider { get; init; }
    [JsonPropertyName("codeActionProvider")] public CodeActionOptions? CodeActionProvider { get; init; }
    [JsonPropertyName("documentFormattingProvider")] public bool DocumentFormattingProvider { get; init; }
}

public sealed record TextDocumentSyncOptions(
    [property: JsonPropertyName("openClose")] bool OpenClose,
    [property: JsonPropertyName("change")] int Change, // 0 none, 1 full, 2 incremental
    [property: JsonPropertyName("save")] SaveOptions Save);

public sealed record SaveOptions(
    [property: JsonPropertyName("includeText")] bool IncludeText);

public sealed record RenameOptions(
    [property: JsonPropertyName("prepareProvider")] bool PrepareProvider);

public sealed record CompletionOptions(
    [property: JsonPropertyName("triggerCharacters")] string[] TriggerCharacters,
    [property: JsonPropertyName("resolveProvider")] bool ResolveProvider);

public sealed record SignatureHelpOptions(
    [property: JsonPropertyName("triggerCharacters")] string[] TriggerCharacters);

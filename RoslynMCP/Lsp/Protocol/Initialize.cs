using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

/// <summary>Initialize params — only the fields the server reads. Unknown fields are ignored
/// by deserialization, which is all LSP forward-compatibility requires.</summary>
public sealed record InitializeParams(
    [property: JsonPropertyName("processId")] int? ProcessId,
    [property: JsonPropertyName("rootUri")] string? RootUri,
    [property: JsonPropertyName("workspaceFolders")] WorkspaceFolder[]? WorkspaceFolders,
    [property: JsonPropertyName("capabilities")] ClientCapabilities? Capabilities,
    /// <summary>The client's <c>roslynSense.*</c> settings, so the first analyzer pass already
    /// runs under them rather than under the defaults until the first change notification.</summary>
    [property: JsonPropertyName("initializationOptions")] System.Text.Json.JsonElement? InitializationOptions = null);

public sealed record DidChangeConfigurationParams(
    [property: JsonPropertyName("settings")] System.Text.Json.JsonElement? Settings);

public sealed record ClientCapabilities(
    [property: JsonPropertyName("textDocument")] TextDocumentClientCapabilities? TextDocument,
    [property: JsonPropertyName("workspace")] WorkspaceClientCapabilities? Workspace = null);

/// <summary>Only the client capabilities the server branches on. A non-null
/// <see cref="Diagnostic"/> means the client pulls diagnostics (LSP 3.17), so the
/// session skips push publishing to avoid duplicate squiggles.</summary>
public sealed record TextDocumentClientCapabilities(
    [property: JsonPropertyName("diagnostic")] System.Text.Json.JsonElement? Diagnostic,
    [property: JsonPropertyName("completion")] CompletionClientCapabilities? Completion = null);

public sealed record CompletionClientCapabilities(
    [property: JsonPropertyName("completionItem")] CompletionItemClientCapabilities? CompletionItem);

public sealed record CompletionItemClientCapabilities(
    [property: JsonPropertyName("snippetSupport")] bool SnippetSupport);

/// <summary>Workspace-side capabilities. The refresh flags say whether the client will honor
/// a server-initiated "re-request everything" nudge; sending one to a client that doesn't
/// support it is an error response, so each is gated.</summary>
public sealed record WorkspaceClientCapabilities(
    [property: JsonPropertyName("codeLens")] RefreshCapability? CodeLens = null,
    [property: JsonPropertyName("inlayHint")] RefreshCapability? InlayHint = null,
    [property: JsonPropertyName("diagnostics")] RefreshCapability? Diagnostics = null);

public sealed record RefreshCapability(
    [property: JsonPropertyName("refreshSupport")] bool RefreshSupport);

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
    [JsonPropertyName("documentRangeFormattingProvider")] public bool DocumentRangeFormattingProvider { get; init; }
    [JsonPropertyName("documentOnTypeFormattingProvider")] public DocumentOnTypeFormattingOptions? DocumentOnTypeFormattingProvider { get; init; }
    [JsonPropertyName("foldingRangeProvider")] public bool FoldingRangeProvider { get; init; }
    [JsonPropertyName("callHierarchyProvider")] public bool CallHierarchyProvider { get; init; }
    [JsonPropertyName("typeHierarchyProvider")] public bool TypeHierarchyProvider { get; init; }
    [JsonPropertyName("semanticTokensProvider")] public SemanticTokensOptions? SemanticTokensProvider { get; init; }
    [JsonPropertyName("diagnosticProvider")] public DiagnosticOptions? DiagnosticProvider { get; init; }
    [JsonPropertyName("codeLensProvider")] public CodeLensOptions? CodeLensProvider { get; init; }
    [JsonPropertyName("executeCommandProvider")] public ExecuteCommandOptions? ExecuteCommandProvider { get; init; }
    [JsonPropertyName("inlayHintProvider")] public InlayHintOptions? InlayHintProvider { get; init; }
    [JsonPropertyName("selectionRangeProvider")] public bool SelectionRangeProvider { get; init; }
    [JsonPropertyName("linkedEditingRangeProvider")] public bool LinkedEditingRangeProvider { get; init; }
    [JsonPropertyName("inlineValueProvider")] public bool InlineValueProvider { get; init; }
    [JsonPropertyName("workspace")] public WorkspaceServerCapabilities? Workspace { get; init; }
}

public sealed record WorkspaceServerCapabilities(
    [property: JsonPropertyName("fileOperations")] FileOperationsServerCapabilities FileOperations);

public sealed record FileOperationsServerCapabilities(
    [property: JsonPropertyName("willRename")] FileOperationRegistration WillRename,
    [property: JsonPropertyName("didCreate")] FileOperationRegistration? DidCreate = null,
    [property: JsonPropertyName("didDelete")] FileOperationRegistration? DidDelete = null);

public sealed record FileOperationRegistration(
    [property: JsonPropertyName("filters")] FileOperationFilter[] Filters);

public sealed record FileOperationFilter(
    [property: JsonPropertyName("scheme")] string Scheme,
    [property: JsonPropertyName("pattern")] FileOperationPattern Pattern);

public sealed record FileOperationPattern(
    [property: JsonPropertyName("glob")] string Glob,
    [property: JsonPropertyName("matches")] string? Matches = null);

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
    [property: JsonPropertyName("triggerCharacters")] string[] TriggerCharacters,
    [property: JsonPropertyName("retriggerCharacters")] string[] RetriggerCharacters);

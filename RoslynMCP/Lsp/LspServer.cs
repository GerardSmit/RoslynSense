using System.Reflection;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using StreamJsonRpc;

namespace RoslynMCP.Lsp;

/// <summary>
/// One LSP session: the JSON-RPC target for a single connected editor. Document sync feeds
/// <see cref="OpenDocumentStore"/> (shared with MCP tools); language features run against the
/// same <see cref="WorkspaceService"/> snapshots MCP tools use.
/// </summary>
internal sealed class LspServer : IDisposable
{
    public string SessionId { get; } = Guid.NewGuid().ToString("N");

    private readonly IServiceProvider _services;
    private JsonRpc? _rpc;
    private DiagnosticsPublisher? _diagnostics;

    public LspServer(IServiceProvider services) => _services = services;

    public void Attach(JsonRpc rpc)
    {
        _rpc = rpc;
        _diagnostics = new DiagnosticsPublisher(rpc);
        LspSessionRegistry.Register(SessionId, rpc);
    }

    // ---- Lifecycle -------------------------------------------------------------------

    [JsonRpcMethod("initialize", UseSingleObjectParameterDeserialization = true)]
    public InitializeResult Initialize(InitializeParams p)
    {
        var capabilities = new ServerCapabilities
        {
            TextDocumentSync = new TextDocumentSyncOptions(
                OpenClose: true,
                Change: 2, // incremental
                Save: new SaveOptions(IncludeText: false)),
            DefinitionProvider = true,
            TypeDefinitionProvider = true,
            ReferencesProvider = true,
            ImplementationProvider = true,
            HoverProvider = true,
            DocumentSymbolProvider = true,
            WorkspaceSymbolProvider = true,
            DocumentHighlightProvider = true,
            RenameProvider = new RenameOptions(PrepareProvider: true),
            CompletionProvider = new CompletionOptions(
                TriggerCharacters: [".", " ", "(", "<", "["],
                ResolveProvider: false),
            CodeActionProvider = true,
            DocumentFormattingProvider = true,
        };

        string? version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
        return new InitializeResult(capabilities, new ServerInfo("RoslynSense", version));
    }

    [JsonRpcMethod("initialized")]
    public void Initialized() { }

    [JsonRpcMethod("shutdown")]
    public object? Shutdown() => null;

    [JsonRpcMethod("exit")]
    public void Exit() => _rpc?.Dispose();

    // ---- Document sync ---------------------------------------------------------------

    [JsonRpcMethod("textDocument/didOpen", UseSingleObjectParameterDeserialization = true)]
    public void DidOpen(DidOpenTextDocumentParams p)
    {
        string path = LspConverters.UriToPath(p.TextDocument.Uri);
        OpenDocumentStore.Open(SessionId, path,
            SourceText.From(p.TextDocument.Text), p.TextDocument.Version);
        _diagnostics?.Schedule(path, immediate: true);
    }

    [JsonRpcMethod("textDocument/didChange", UseSingleObjectParameterDeserialization = true)]
    public void DidChange(DidChangeTextDocumentParams p)
    {
        string path = LspConverters.UriToPath(p.TextDocument.Uri);
        var result = OpenDocumentStore.Change(path, p.TextDocument.Version, text =>
        {
            foreach (var change in p.ContentChanges)
            {
                text = change.Range is null
                    ? SourceText.From(change.Text)
                    : text.WithChanges(new TextChange(LspConverters.ToTextSpan(text, change.Range), change.Text));
            }
            return text;
        });

        if (result is not null)
            _diagnostics?.Schedule(path, immediate: false);
    }

    [JsonRpcMethod("textDocument/didSave", UseSingleObjectParameterDeserialization = true)]
    public void DidSave(DidSaveTextDocumentParams p)
    {
        _diagnostics?.Schedule(LspConverters.UriToPath(p.TextDocument.Uri), immediate: true);
    }

    [JsonRpcMethod("textDocument/didClose", UseSingleObjectParameterDeserialization = true)]
    public void DidClose(DidCloseTextDocumentParams p)
    {
        string path = LspConverters.UriToPath(p.TextDocument.Uri);
        OpenDocumentStore.Close(SessionId, path);
        _diagnostics?.Clear(path);
    }

    // ---- Language features -----------------------------------------------------------

    [JsonRpcMethod("textDocument/definition", UseSingleObjectParameterDeserialization = true)]
    public Task<Location[]> Definition(TextDocumentPositionParams p, CancellationToken ct) =>
        Handlers.NavigationHandlers.DefinitionAsync(p, typeDefinition: false, ct);

    [JsonRpcMethod("textDocument/typeDefinition", UseSingleObjectParameterDeserialization = true)]
    public Task<Location[]> TypeDefinition(TextDocumentPositionParams p, CancellationToken ct) =>
        Handlers.NavigationHandlers.DefinitionAsync(p, typeDefinition: true, ct);

    [JsonRpcMethod("textDocument/references", UseSingleObjectParameterDeserialization = true)]
    public Task<Location[]> References(ReferenceParams p, CancellationToken ct) =>
        Handlers.NavigationHandlers.ReferencesAsync(p, ct);

    [JsonRpcMethod("textDocument/implementation", UseSingleObjectParameterDeserialization = true)]
    public Task<Location[]> Implementation(TextDocumentPositionParams p, CancellationToken ct) =>
        Handlers.NavigationHandlers.ImplementationAsync(p, ct);

    [JsonRpcMethod("textDocument/hover", UseSingleObjectParameterDeserialization = true)]
    public Task<Hover?> Hover(TextDocumentPositionParams p, CancellationToken ct) =>
        Handlers.HoverHandler.HoverAsync(p, ct);

    [JsonRpcMethod("textDocument/documentHighlight", UseSingleObjectParameterDeserialization = true)]
    public Task<DocumentHighlight[]> DocumentHighlight(TextDocumentPositionParams p, CancellationToken ct) =>
        Handlers.NavigationHandlers.DocumentHighlightAsync(p, ct);

    [JsonRpcMethod("textDocument/documentSymbol", UseSingleObjectParameterDeserialization = true)]
    public Task<DocumentSymbol[]> DocumentSymbol(DocumentSymbolParams p, CancellationToken ct) =>
        Handlers.SymbolHandlers.DocumentSymbolsAsync(p, ct);

    [JsonRpcMethod("workspace/symbol", UseSingleObjectParameterDeserialization = true)]
    public Task<SymbolInformation[]> WorkspaceSymbol(WorkspaceSymbolParams p, CancellationToken ct) =>
        Handlers.SymbolHandlers.WorkspaceSymbolsAsync(p, ct);

    [JsonRpcMethod("textDocument/prepareRename", UseSingleObjectParameterDeserialization = true)]
    public Task<PrepareRenameResult?> PrepareRename(TextDocumentPositionParams p, CancellationToken ct) =>
        Handlers.RenameHandler.PrepareRenameAsync(p, ct);

    [JsonRpcMethod("textDocument/rename", UseSingleObjectParameterDeserialization = true)]
    public Task<WorkspaceEdit?> Rename(RenameParams p, CancellationToken ct) =>
        Handlers.RenameHandler.RenameAsync(p, ct);

    [JsonRpcMethod("textDocument/completion", UseSingleObjectParameterDeserialization = true)]
    public Task<CompletionList> Completion(CompletionParams p, CancellationToken ct) =>
        Handlers.CompletionHandler.CompletionAsync(p, ct);

    [JsonRpcMethod("textDocument/codeAction", UseSingleObjectParameterDeserialization = true)]
    public Task<Protocol.CodeAction[]> CodeAction(CodeActionParams p, CancellationToken ct) =>
        Handlers.CodeActionHandler.CodeActionsAsync(p, ct);

    [JsonRpcMethod("textDocument/formatting", UseSingleObjectParameterDeserialization = true)]
    public Task<TextEdit[]> Formatting(DocumentFormattingParams p, CancellationToken ct) =>
        Handlers.FormattingHandler.FormatAsync(p, ct);

    public void Dispose()
    {
        LspSessionRegistry.Unregister(SessionId);
        OpenDocumentStore.CloseSession(SessionId);
        _diagnostics?.Dispose();
    }
}

using System.Reflection;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Config;
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
    private readonly LspResolveCache _resolveCache = new();
    private JsonRpc? _rpc;
    private DiagnosticsPublisher? _diagnostics;
    private bool _clientPullsDiagnostics;
    private bool _clientRefreshesCodeLens;
    private bool _clientRefreshesInlayHints;
    private CancellationTokenSource? _refreshDebounce;

    /// <summary>Nudges the client to re-request derived data after a change whose effects reach
    /// beyond the edited document: diagnostics (cross-file), code lens reference counts, and
    /// inlay hints all go stale on an edit somewhere else. Debounced — mirrors the ~2s batching
    /// in Roslyn's own LSP server. The client re-pulls the changed document itself immediately;
    /// this only covers everything else.</summary>
    private void ScheduleClientRefresh(RefreshKind kinds = RefreshKind.All)
    {
        _refreshDebounce?.Cancel();
        var cts = _refreshDebounce = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
                await RefreshClientAsync(kinds, cts.Token);
            }
            catch (Exception)
            {
                // Cancelled by a newer edit, or the client went away.
            }
        });
    }

    /// <summary>Sends the refresh requests the client actually declared support for.
    /// Unsupported ones are skipped rather than sent-and-swallowed, because an unknown method
    /// is an error response the client may log as a server fault.</summary>
    internal async Task RefreshClientAsync(RefreshKind kinds, CancellationToken ct = default)
    {
        if (_rpc is not { } rpc)
            return;

        if (kinds.HasFlag(RefreshKind.Diagnostics) && _clientPullsDiagnostics)
            await InvokeRefreshAsync(rpc, "workspace/diagnostic/refresh", ct);
        if (kinds.HasFlag(RefreshKind.CodeLens) && _clientRefreshesCodeLens)
            await InvokeRefreshAsync(rpc, "workspace/codeLens/refresh", ct);
        if (kinds.HasFlag(RefreshKind.InlayHint) && _clientRefreshesInlayHints)
            await InvokeRefreshAsync(rpc, "workspace/inlayHint/refresh", ct);
    }

    private static async Task InvokeRefreshAsync(JsonRpc rpc, string method, CancellationToken ct)
    {
        try { await rpc.InvokeWithCancellationAsync(method, cancellationToken: ct); }
        catch (Exception ex) when (ex is RemoteInvocationException or ConnectionLostException or ObjectDisposedException)
        {
            // Client refused or disconnected — refreshes are advisory.
        }
    }

    public LspServer(IServiceProvider services) => _services = services;

    public void Attach(JsonRpc rpc)
    {
        _rpc = rpc;
        _diagnostics = new DiagnosticsPublisher(rpc);
        LspSessionRegistry.Register(SessionId, rpc, this);
        LspProgress.Install();
        LspLog.Install();
    }

    // ---- Lifecycle -------------------------------------------------------------------

    [JsonRpcMethod("initialize", UseSingleObjectParameterDeserialization = true)]
    public InitializeResult Initialize(InitializeParams p)
    {
        // Pull-capable clients (LSP 3.17) get pull diagnostics only — pushing too would
        // draw duplicate squiggles.
        _clientPullsDiagnostics = p.Capabilities?.TextDocument?.Diagnostic is not null;
        _clientRefreshesCodeLens = p.Capabilities?.Workspace?.CodeLens?.RefreshSupport ?? false;
        _clientRefreshesInlayHints = p.Capabilities?.Workspace?.InlayHint?.RefreshSupport ?? false;
        LspClientState.SnippetSupport =
            p.Capabilities?.TextDocument?.Completion?.CompletionItem?.SnippetSupport ?? false;

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
            // No " " or "(" triggers: space fires on every keystroke boundary (junk requests
            // that burn Roslyn's provider time budgets), "(" belongs to signature help.
            CompletionProvider = new CompletionOptions(
                TriggerCharacters: [".", "["],
                ResolveProvider: true),
            SignatureHelpProvider = new SignatureHelpOptions(
                TriggerCharacters: ["(", ",", "<"],
                RetriggerCharacters: [")", "]", ">"]),
            CodeActionProvider = new Protocol.CodeActionOptions(ResolveProvider: true),
            DocumentFormattingProvider = true,
            DocumentRangeFormattingProvider = true,
            DocumentOnTypeFormattingProvider = new DocumentOnTypeFormattingOptions(
                FirstTriggerCharacter: ";",
                MoreTriggerCharacter: ["}", "\n"]),
            FoldingRangeProvider = true,
            CallHierarchyProvider = true,
            TypeHierarchyProvider = true,
            SemanticTokensProvider = new SemanticTokensOptions(
                new SemanticTokensLegend(
                    Handlers.SemanticTokensHandler.TokenTypes,
                    Handlers.SemanticTokensHandler.TokenModifiers),
                Full: true),
            DiagnosticProvider = new DiagnosticOptions(
                InterFileDependencies: true,
                WorkspaceDiagnostics: LspFeatureOptions.WorkspaceDiagnosticsScope != "off"),
            CodeLensProvider = new CodeLensOptions(ResolveProvider: true),
            ExecuteCommandProvider = new ExecuteCommandOptions(Handlers.ExecuteCommandHandler.Commands),
            InlayHintProvider = new InlayHintOptions(ResolveProvider: false),
            // Renaming a .cs file should rename the type inside it. Returning the edit from
            // willRename puts it in the same undo step as the rename itself.
            Workspace = new WorkspaceServerCapabilities(
                new FileOperationsServerCapabilities(
                    new FileOperationRegistration(
                        [new FileOperationFilter("file", new FileOperationPattern("**/*.cs", "file"))]))),
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
        if (!_clientPullsDiagnostics)
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

        if (result is not null && !_clientPullsDiagnostics)
            _diagnostics?.Schedule(path, immediate: false);
        if (result is not null)
            ScheduleClientRefresh();
    }

    [JsonRpcMethod("textDocument/didSave", UseSingleObjectParameterDeserialization = true)]
    public void DidSave(DidSaveTextDocumentParams p)
    {
        if (!_clientPullsDiagnostics)
            _diagnostics?.Schedule(LspConverters.UriToPath(p.TextDocument.Uri), immediate: true);
        ScheduleClientRefresh();
    }

    // ---- Launch and debug -------------------------------------------------------------

    [JsonRpcMethod("roslynSense/debuggerPath")]
    public Task<DebuggerPathResult> DebuggerPath(CancellationToken ct) =>
        Handlers.LaunchHandler.DebuggerPathAsync(ct);

    [JsonRpcMethod("roslynSense/launchTargets", UseSingleObjectParameterDeserialization = true)]
    public Task<LaunchTarget[]> LaunchTargets(LaunchTargetsParams p, CancellationToken ct) =>
        Handlers.LaunchHandler.LaunchTargetsAsync(p, ct);

    [JsonRpcMethod("roslynSense/attachTargets")]
    public AttachTarget[] AttachTargets() => Handlers.LaunchHandler.AttachTargets();

    // ---- Solution Explorer --------------------------------------------------------------

    [JsonRpcMethod("roslynSense/solutionTree", UseSingleObjectParameterDeserialization = true)]
    public Task<SolutionTreeNode[]> SolutionTree(SolutionTreeParams p, CancellationToken ct) =>
        Handlers.SolutionTreeHandler.ChildrenAsync(p, ct);

    // ---- Packages -----------------------------------------------------------------------

    [JsonRpcMethod("roslynSense/nuget/search", UseSingleObjectParameterDeserialization = true)]
    public Task<Handlers.PackageSummaryDto[]> NuGetSearch(Handlers.NuGetSearchParams p, CancellationToken ct) =>
        Handlers.NuGetHandler.SearchAsync(p, ct);

    [JsonRpcMethod("roslynSense/nuget/versions", UseSingleObjectParameterDeserialization = true)]
    public Task<IReadOnlyList<string>> NuGetVersions(Handlers.NuGetVersionsParams p, CancellationToken ct) =>
        Handlers.NuGetHandler.VersionsAsync(p, ct);

    [JsonRpcMethod("roslynSense/nuget/installed")]
    public Task<Handlers.ProjectPackagesDto[]> NuGetInstalled(CancellationToken ct) =>
        Handlers.NuGetHandler.InstalledAsync(ct);

    [JsonRpcMethod("roslynSense/nuget/updates", UseSingleObjectParameterDeserialization = true)]
    public Task<Handlers.PackageSummaryDto[]> NuGetUpdates(Handlers.NuGetUpdatesParams p, CancellationToken ct) =>
        Handlers.NuGetHandler.UpdatesAsync(p, ct);

    [JsonRpcMethod("roslynSense/nuget/consolidations")]
    public Task<Handlers.ConsolidationDto[]> NuGetConsolidations(CancellationToken ct) =>
        Handlers.NuGetHandler.ConsolidationsAsync(ct);

    [JsonRpcMethod("roslynSense/nuget/sources")]
    public string[] NuGetSources() => Handlers.NuGetHandler.Sources();

    [JsonRpcMethod("roslynSense/nuget/install", UseSingleObjectParameterDeserialization = true)]
    public Task<Handlers.PackageOperationDto> NuGetInstall(Handlers.NuGetOperationParams p, CancellationToken ct) =>
        Handlers.NuGetHandler.InstallAsync(p, ct);

    [JsonRpcMethod("roslynSense/nuget/update", UseSingleObjectParameterDeserialization = true)]
    public Task<Handlers.PackageOperationDto> NuGetUpdate(Handlers.NuGetOperationParams p, CancellationToken ct) =>
        Handlers.NuGetHandler.InstallAsync(p, ct);

    [JsonRpcMethod("roslynSense/nuget/uninstall", UseSingleObjectParameterDeserialization = true)]
    public Task<Handlers.PackageOperationDto> NuGetUninstall(Handlers.NuGetOperationParams p, CancellationToken ct) =>
        Handlers.NuGetHandler.UninstallAsync(p, ct);

    [JsonRpcMethod("roslynSense/nuget/consolidate", UseSingleObjectParameterDeserialization = true)]
    public Task<Handlers.PackageOperationDto> NuGetConsolidate(Handlers.NuGetOperationParams p, CancellationToken ct) =>
        Handlers.NuGetHandler.ConsolidateAsync(p, ct);

    // ---- Tests ------------------------------------------------------------------------

    [JsonRpcMethod("roslynSense/testProjects")]
    public Task<TestProjectInfo[]> TestProjects(CancellationToken ct) =>
        Handlers.TestHandler.ProjectsAsync(ct);

    [JsonRpcMethod("roslynSense/testDiscover", UseSingleObjectParameterDeserialization = true)]
    public Task<TestInfo[]> TestDiscover(TestDiscoverParams p, CancellationToken ct) =>
        Handlers.TestHandler.DiscoverAsync(p, ct);

    [JsonRpcMethod("roslynSense/testRun", UseSingleObjectParameterDeserialization = true)]
    public Task<TestResultInfo[]> TestRun(TestRunParams p, CancellationToken ct) =>
        Handlers.TestHandler.RunAsync(p, ct);

    [JsonRpcMethod("roslynSense/testDebug", UseSingleObjectParameterDeserialization = true)]
    public Task<TestDebugResult> TestDebug(TestDebugParams p, CancellationToken ct) =>
        Handlers.TestHandler.DebugAsync(p, ct);

    [JsonRpcMethod("roslynSense/testCoverage", UseSingleObjectParameterDeserialization = true)]
    public FileCoverageInfo[] TestCoverage(TestCoverageParams p) =>
        Handlers.TestHandler.Coverage(p);

    [JsonRpcMethod("workspace/diagnostic", UseSingleObjectParameterDeserialization = true)]
    public Task<WorkspaceDiagnosticReport> WorkspaceDiagnostic(
        WorkspaceDiagnosticParams p, CancellationToken ct) =>
        Handlers.WorkspaceDiagnosticsHandler.DiagnoseAsync(p, ct);

    [JsonRpcMethod("roslynSense/editorContext", UseSingleObjectParameterDeserialization = true)]
    public void EditorContext(Handlers.EditorContextParams p) =>
        Handlers.EditorContextHandler.Report(p);

    [JsonRpcMethod("workspace/willRenameFiles", UseSingleObjectParameterDeserialization = true)]
    public Task<WorkspaceEdit?> WillRenameFiles(Handlers.RenameFilesParams p, CancellationToken ct) =>
        Handlers.FileOperationsHandler.WillRenameAsync(p, ct);

    [JsonRpcMethod("workspace/didChangeWatchedFiles", UseSingleObjectParameterDeserialization = true)]
    public void DidChangeWatchedFiles(DidChangeWatchedFilesParams p) =>
        Handlers.WatchedFilesHandler.Handle(p);

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

    [JsonRpcMethod("textDocument/signatureHelp", UseSingleObjectParameterDeserialization = true)]
    public Task<Protocol.SignatureHelp?> SignatureHelp(SignatureHelpParams p, CancellationToken ct) =>
        Handlers.SignatureHelpHandler.SignatureHelpAsync(p, ct);

    [JsonRpcMethod("textDocument/completion", UseSingleObjectParameterDeserialization = true)]
    public Task<CompletionList> Completion(CompletionParams p, CancellationToken ct) =>
        Handlers.CompletionHandler.CompletionAsync(p, _resolveCache, ct);

    [JsonRpcMethod("completionItem/resolve", UseSingleObjectParameterDeserialization = true)]
    public Task<CompletionItem> CompletionResolve(CompletionItem item, CancellationToken ct) =>
        Handlers.CompletionHandler.ResolveAsync(item, _resolveCache, ct);

    [JsonRpcMethod("textDocument/codeAction", UseSingleObjectParameterDeserialization = true)]
    public Task<Protocol.CodeAction[]> CodeAction(CodeActionParams p, CancellationToken ct) =>
        Handlers.CodeActionHandler.CodeActionsAsync(p, _resolveCache, ct);

    [JsonRpcMethod("codeAction/resolve", UseSingleObjectParameterDeserialization = true)]
    public Task<Protocol.CodeAction> CodeActionResolve(Protocol.CodeAction action, CancellationToken ct) =>
        Handlers.CodeActionHandler.ResolveAsync(action, _resolveCache, ct);

    [JsonRpcMethod("textDocument/formatting", UseSingleObjectParameterDeserialization = true)]
    public Task<TextEdit[]> Formatting(DocumentFormattingParams p, CancellationToken ct) =>
        Handlers.FormattingHandler.FormatAsync(p, ct);

    [JsonRpcMethod("textDocument/rangeFormatting", UseSingleObjectParameterDeserialization = true)]
    public Task<TextEdit[]> RangeFormatting(DocumentRangeFormattingParams p, CancellationToken ct) =>
        Handlers.FormattingHandler.FormatRangeAsync(p, ct);

    [JsonRpcMethod("textDocument/onTypeFormatting", UseSingleObjectParameterDeserialization = true)]
    public Task<TextEdit[]> OnTypeFormatting(DocumentOnTypeFormattingParams p, CancellationToken ct) =>
        Handlers.FormattingHandler.FormatOnTypeAsync(p, ct);

    [JsonRpcMethod("textDocument/foldingRange", UseSingleObjectParameterDeserialization = true)]
    public Task<Protocol.FoldingRange[]> FoldingRange(FoldingRangeParams p, CancellationToken ct) =>
        Handlers.FoldingRangeHandler.FoldingRangesAsync(p, ct);

    [JsonRpcMethod("textDocument/prepareCallHierarchy", UseSingleObjectParameterDeserialization = true)]
    public Task<HierarchyItem[]> PrepareCallHierarchy(TextDocumentPositionParams p, CancellationToken ct) =>
        Handlers.CallHierarchyHandler.PrepareAsync(p, ct);

    [JsonRpcMethod("callHierarchy/incomingCalls", UseSingleObjectParameterDeserialization = true)]
    public Task<CallHierarchyIncomingCall[]> IncomingCalls(CallHierarchyCallsParams p, CancellationToken ct) =>
        Handlers.CallHierarchyHandler.IncomingCallsAsync(p, ct);

    [JsonRpcMethod("callHierarchy/outgoingCalls", UseSingleObjectParameterDeserialization = true)]
    public Task<CallHierarchyOutgoingCall[]> OutgoingCalls(CallHierarchyCallsParams p, CancellationToken ct) =>
        Handlers.CallHierarchyHandler.OutgoingCallsAsync(p, ct);

    [JsonRpcMethod("textDocument/prepareTypeHierarchy", UseSingleObjectParameterDeserialization = true)]
    public Task<HierarchyItem[]> PrepareTypeHierarchy(TextDocumentPositionParams p, CancellationToken ct) =>
        Handlers.TypeHierarchyHandler.PrepareAsync(p, ct);

    [JsonRpcMethod("typeHierarchy/supertypes", UseSingleObjectParameterDeserialization = true)]
    public Task<HierarchyItem[]> Supertypes(TypeHierarchyItemParams p, CancellationToken ct) =>
        Handlers.TypeHierarchyHandler.SupertypesAsync(p, ct);

    [JsonRpcMethod("typeHierarchy/subtypes", UseSingleObjectParameterDeserialization = true)]
    public Task<HierarchyItem[]> Subtypes(TypeHierarchyItemParams p, CancellationToken ct) =>
        Handlers.TypeHierarchyHandler.SubtypesAsync(p, ct);

    [JsonRpcMethod("textDocument/semanticTokens/full", UseSingleObjectParameterDeserialization = true)]
    public Task<SemanticTokens> SemanticTokensFull(SemanticTokensParams p, CancellationToken ct) =>
        Handlers.SemanticTokensHandler.SemanticTokensFullAsync(p, ct);

    [JsonRpcMethod("textDocument/diagnostic", UseSingleObjectParameterDeserialization = true)]
    public Task<object> Diagnostic(DocumentDiagnosticParams p, CancellationToken ct) =>
        Handlers.DiagnosticsHandler.PullAsync(p, ct);

    [JsonRpcMethod("textDocument/codeLens", UseSingleObjectParameterDeserialization = true)]
    public Task<Protocol.CodeLens[]> CodeLens(CodeLensParams p, CancellationToken ct) =>
        Handlers.CodeLensHandler.CodeLensAsync(p, ct);

    [JsonRpcMethod("codeLens/resolve", UseSingleObjectParameterDeserialization = true)]
    public Task<Protocol.CodeLens> CodeLensResolve(Protocol.CodeLens lens, CancellationToken ct) =>
        Handlers.CodeLensHandler.ResolveAsync(lens, ct);

    [JsonRpcMethod("workspace/executeCommand", UseSingleObjectParameterDeserialization = true)]
    public Task<object> ExecuteCommand(ExecuteCommandParams p, CancellationToken ct) =>
        Handlers.ExecuteCommandHandler.ExecuteAsync(p, ct);

    [JsonRpcMethod("textDocument/inlayHint", UseSingleObjectParameterDeserialization = true)]
    public Task<InlayHint[]> InlayHint(InlayHintParams p, CancellationToken ct) =>
        Handlers.InlayHintHandler.InlayHintsAsync(p, ct);

    [JsonRpcMethod("roslynSense/onAutoInsert", UseSingleObjectParameterDeserialization = true)]
    public Task<OnAutoInsertResult?> OnAutoInsert(OnAutoInsertParams p, CancellationToken ct) =>
        Handlers.OnAutoInsertHandler.OnAutoInsertAsync(p, ct);

    [JsonRpcMethod("roslynSense/inheritanceMarkers", UseSingleObjectParameterDeserialization = true)]
    public Task<InheritanceMarker[]> InheritanceMarkers(InheritanceMarkersParams p, CancellationToken ct) =>
        Handlers.InheritanceMarkersHandler.MarkersAsync(p, ct);

    [JsonRpcMethod("roslynSense/resolveInheritanceTarget", UseSingleObjectParameterDeserialization = true)]
    public Task<Location?> ResolveInheritanceTarget(ResolveInheritanceTargetParams p, CancellationToken ct) =>
        Handlers.InheritanceMarkersHandler.ResolveTargetAsync(p, ct);

    [JsonRpcMethod("roslynSense/runningProcesses")]
    public RunningProcess[] RunningProcesses() =>
        Services.Run.RunningProcessRegistry.List()
            .Select(e => new RunningProcess(
                e.SessionId, e.Pid,
                Path.GetFileNameWithoutExtension(e.ProjectPath),
                e.ProjectPath, e.Url, e.StartedAtUtc.ToString("O")))
            .ToArray();

    [JsonRpcMethod("roslynSense/killProcess", UseSingleObjectParameterDeserialization = true)]
    public string KillProcess(KillProcessParams p) =>
        Services.Run.RunningProcessRegistry.Kill(p.Pid);

    // ---- Debug bridge ----------------------------------------------------------------

    [JsonRpcMethod("roslynSense/debugSessions")]
    public DebugSessionInfo[] DebugSessions() => Handlers.DebugBridgeHandler.Sessions();

    [JsonRpcMethod("roslynSense/debugCommand", UseSingleObjectParameterDeserialization = true)]
    public Task<DebugCommandResult> DebugCommand(DebugCommandParams p, CancellationToken ct) =>
        Handlers.DebugBridgeHandler.CommandAsync(p, ct);

    [JsonRpcMethod("roslynSense/editorDebugState", UseSingleObjectParameterDeserialization = true)]
    public void EditorDebugState(EditorDebugStateParams p) =>
        Handlers.DebugBridgeHandler.EditorState(p);

    [JsonRpcMethod("roslynSense/syncBreakpoints", UseSingleObjectParameterDeserialization = true)]
    public void SyncBreakpoints(SyncBreakpointsParams p) =>
        Handlers.DebugBridgeHandler.SyncBreakpoints(p);

    public void Dispose()
    {
        LspSessionRegistry.Unregister(SessionId);
        OpenDocumentStore.CloseSession(SessionId);
        _diagnostics?.Dispose();
        _refreshDebounce?.Cancel();
    }
}

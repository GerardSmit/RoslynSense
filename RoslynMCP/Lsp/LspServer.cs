using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Config;
using RoslynMCP.Languages;
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

    /// <summary>
    /// The language packs this connection has switched on. An instance field, never a static:
    /// the daemon serves several editors from one container, and one window's language settings
    /// must not deactivate a pack under another. Replaced in <see cref="Initialize"/>; until then
    /// — and for a server constructed without services, as tests do — every request is C#.
    /// </summary>
    private LanguageSession _languages = LanguageSession.Empty;
    private JsonRpc? _rpc;
    private DiagnosticsPublisher? _diagnostics;
    /// <summary>
    /// Set from the client's initialization options and read on every code-action request: it
    /// decides whether a Roslyn action group is collapsed to one entry with a picker or flattened
    /// into its children. Per connection, because two windows on the same daemon can be different
    /// editors.
    /// </summary>
    private bool _clientPicksNestedActions;

    private bool _clientPullsDiagnostics;
    private bool _clientRefreshesCodeLens;
    private bool _clientRefreshesInlayHints;
    private readonly Services.Debouncer _refreshDebounce = new("Lsp");

    /// <summary>Nudges the client to re-request derived data after a change whose effects reach
    /// beyond the edited document: diagnostics (cross-file), code lens reference counts, and
    /// inlay hints all go stale on an edit somewhere else. Debounced — mirrors the ~2s batching
    /// in Roslyn's own LSP server. The client re-pulls the changed document itself immediately;
    /// this only covers everything else.</summary>
    private void ScheduleClientRefresh(RefreshKind kinds = RefreshKind.All) =>
        _refreshDebounce.Restart(TimeSpan.FromSeconds(2), async ct =>
        {
            try
            {
                await RefreshClientAsync(kinds, ct);
            }
            catch (Exception)
            {
                // The client went away mid-send; not worth a log line per detach.
            }
        });

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

    /// <summary>
    /// C#'s own completion triggers. "(" opens the list on <c>if (</c>, <c>while (</c>,
    /// <c>switch (</c> and a call's first argument, which is where VS and Rider open theirs; it is
    /// a signature-help trigger as well, and the two coexist there the same way. Still no " ":
    /// Roslyn accepts a space almost anywhere a statement can continue — after a plain <c>;</c>
    /// included — so registering it means a request per keystroke boundary, burning the provider
    /// time budgets that decide how complete the next real list is.
    /// </summary>
    private static readonly string[] CSharpCompletionTriggers = [".", "(", "["];

    /// <summary>C#'s own signature-help triggers; "&lt;" opens a type argument list.</summary>
    private static readonly string[] CSharpSignatureHelpTriggers = ["(", ",", "<"];

    /// <summary>The only files C# itself owns.</summary>
    private const string CSharpFileGlob = "**/*.cs";

    public LspServer(IServiceProvider services) => _services = services;

    public void Attach(JsonRpc rpc)
    {
        _rpc = rpc;
        _diagnostics = new DiagnosticsPublisher(rpc);
        LspSessionRegistry.Register(SessionId, rpc, this);
        LspProgress.Install();
        LspWorkspaceRefresh.Install();
        WorkspaceService.InstallOpenBufferBridge();
        LspLog.Install();
        LspNuGetCredentials.Install();
        Handlers.NuGetHandler.InstallMutationHook();
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
        // Opt-in per field: a client lists the itemDefaults members it honors, and hoisting one it
        // did not list would drop it on the floor. Only editRange is hoisted today, so only
        // editRange is asked about.
        LspClientState.CompletionEditRangeDefault =
            p.Capabilities?.TextDocument?.Completion?.CompletionList?.ItemDefaults is { } itemDefaults
            && Array.IndexOf(itemDefaults, "editRange") >= 0;

        // Before the capabilities are built: workspaceDiagnostics decides one of them.
        Handlers.ConfigurationHandler.Apply(p.InitializationOptions);

        // After it, because the client's initialization options say which packs this connection
        // wants; before the capabilities, because the enabled set decides what they advertise.
        // Resolved through the registry rather than as a bare collection: constructing it is also
        // what publishes it to the handlers that run outside DI. A server built without services
        // — every test that constructs one directly — gets pure C#.
        bool registerCommands = Handlers.ConfigurationHandler.ReadRegisterCommands(p.InitializationOptions);
        _clientPicksNestedActions =
            Handlers.ConfigurationHandler.ReadNestedCodeActions(p.InitializationOptions);

        var activation = Handlers.ConfigurationHandler.ReadLanguages(p.InitializationOptions);
        _languages = new LanguageSession(
            (_services.GetService(typeof(LanguageRegistry)) as LanguageRegistry ?? LanguageRegistry.Empty).Packs,
            pack => activation.IsEnabled(pack.Id));

        // The publisher was built at Attach, before the client had said which languages it wants.
        // Without this, push diagnostics — the path clients without pull support use — would be the
        // one surface the per-window toggle never reached.
        if (_diagnostics is { } diagnostics)
            diagnostics.Languages = _languages;

        // Designer regeneration was armed only by the MCP open_solution tool, so a control added
        // to markup in the editor never got its code-behind field.
        DesignerWatchBridge.Start(_services, p);

        // One registration serves willRename, didCreate and didDelete: the set of files the server
        // wants to hear about is the same for all three.
        var fileOperations = FileOperations();

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
                TriggerCharacters: TriggerCharacters(
                    CSharpCompletionTriggers, static c => c.CompletionTriggerCharacters),
                ResolveProvider: true),
            SignatureHelpProvider = new SignatureHelpOptions(
                TriggerCharacters: TriggerCharacters(
                    CSharpSignatureHelpTriggers, static c => c.SignatureHelpTriggerCharacters),
                RetriggerCharacters: [")", "]", ">"]),
            CodeActionProvider = new Protocol.CodeActionOptions(ResolveProvider: true),
            DocumentFormattingProvider = true,
            DocumentRangeFormattingProvider = true,
            // "{" is what moves an opening brace onto its own line as it is typed, and newline
            // repeats that when the editor cancelled it — and does nothing else, because an
            // edit reaching the fresh line unindents it. See FormatOnTypeAsync.
            DocumentOnTypeFormattingProvider = new DocumentOnTypeFormattingOptions(
                FirstTriggerCharacter: ";",
                MoreTriggerCharacter: ["}", "{", "\n"]),
            FoldingRangeProvider = true,
            CallHierarchyProvider = true,
            TypeHierarchyProvider = true,
            // Delta and range both matter on a large file: an edit otherwise re-sends every
            // token in it, and opening one classifies the whole file before anything paints.
            // The legend is the session's, not C#'s: a pack appends its own token types after
            // Roslyn's, and which packs this connection enabled decides the numbering.
            SemanticTokensProvider = new SemanticTokensOptions(
                _languages.Legend,
                Full: new SemanticTokensFullOptions(Delta: true),
                Range: true),
            DiagnosticProvider = new DiagnosticOptions(
                InterFileDependencies: true,
                WorkspaceDiagnostics: LspFeatureOptions.WorkspaceDiagnosticsScope != "off"),
            CodeLensProvider = new CodeLensOptions(ResolveProvider: true),
            // A command the client can see is a command it can put on a menu, so a pack's commands
            // are advertised only while that pack is enabled — and only to the connection that
            // asked for them, since an editor has one command table and a duplicate id fails the
            // whole connection.
            ExecuteCommandProvider = registerCommands
                ? new ExecuteCommandOptions(
                    [
                        .. Handlers.ExecuteCommandHandler.Commands,
                        .. _languages.Packs.SelectMany(pack => pack.Capabilities.Commands),
                    ])
                : null,
            InlayHintProvider = new InlayHintOptions(ResolveProvider: false),
            SelectionRangeProvider = true,
            LinkedEditingRangeProvider = true,
            InlineValueProvider = true,
            // Nothing in C# is a link; the targets a document names — a master page, a user
            // control's Src, a stylesheet — are a markup idea, so the capability follows the packs.
            // Every link a pack returns carries its target, so there is nothing left to resolve.
            DocumentLinkProvider = _languages.Contributors<ILanguageDocumentLinkProvider>().Count > 0
                ? new DocumentLinkOptions(ResolveProvider: false)
                : null,
            Workspace = new WorkspaceServerCapabilities(
                new FileOperationsServerCapabilities(
                    WillRename: fileOperations,
                    DidCreate: fileOperations,
                    DidDelete: fileOperations)),
        };

        string? version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
        return new InitializeResult(capabilities, new ServerInfo("RoslynSense", version));
    }

    /// <summary>
    /// C#'s trigger characters widened by every enabled pack's, deduplicated and with C#'s first.
    /// </summary>
    /// <remarks>
    /// A union rather than a per-language registration because the protocol gives the whole server
    /// one list. The asymmetry is what makes the union the safe direction: an extra character costs
    /// a request the handler declines — Roslyn runs its own <c>ShouldTriggerCompletion</c> before
    /// doing any work — while a missing one means the editor never asks at all.
    /// </remarks>
    private string[] TriggerCharacters(
        string[] csharp, Func<LanguageCapabilities, ImmutableArray<string>> select)
    {
        var union = new List<string>(csharp);

        foreach (var pack in _languages.Packs)
        {
            foreach (string character in select(pack.Capabilities))
            {
                if (!union.Contains(character))
                    union.Add(character);
            }
        }

        return [.. union];
    }

    /// <summary>
    /// The files the server wants file-operation notifications about: C#'s, plus each enabled
    /// pack's.
    /// </summary>
    /// <remarks>
    /// Renaming a <c>.cs</c> file should rename the type inside it, and returning that edit from
    /// <c>willRename</c> puts it in the same undo step as the rename itself. Create and delete are
    /// after the fact: a new file needs its namespace, a deleted one needs to leave its project's
    /// item list. A pack's files want the same three, which is why the glob list is the only part
    /// that varies.
    /// </remarks>
    private FileOperationRegistration FileOperations()
    {
        var globs = new List<string> { CSharpFileGlob };

        foreach (var pack in _languages.Packs)
            globs.AddRange(pack.Capabilities.FileOperationGlobs);

        return new FileOperationRegistration(
            [.. globs.Select(glob => new FileOperationFilter("file", new FileOperationPattern(glob, "file")))]);
    }

    /// <remarks>
    /// The one thing done here rather than at <c>initialize</c>: the client is only ready to be
    /// shown work-done progress once it has sent this, and a solution load is the longest thing
    /// this server ever does unprompted.
    /// </remarks>
    [JsonRpcMethod("initialized")]
    public void Initialized() => _ = SolutionWarmup.Start();

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

        // A project opened cold has no import-completion index, and completion no longer builds
        // one on its own thread (see CompletionHandler.s_options) — start it now, before the
        // first Ctrl+Space needs it.
        ImportCompletionWarmer.Schedule(path, immediate: true);
    }

    /// <remarks>
    /// A ranged change is a delta against the text the editor believes this server holds. When that
    /// belief is wrong the delta lands at the wrong offsets, and nothing afterwards puts it right:
    /// didSave declares <c>includeText: false</c>, so there is no point at which the full document
    /// is re-sent. The mirror stays wrong until the file is closed and reopened, and every hover,
    /// completion, rename and code action in between is computed against text that exists nowhere.
    ///
    /// Two ways the belief can be wrong, and the store refuses the edit for both rather than
    /// applying it to the wrong base — the file then reads from disk, which is wrong but converges
    /// on the next save, instead of being wrong permanently:
    /// a version that did not advance (a duplicate or reordered notification), and a range the
    /// buffer cannot hold (which used to throw out of this handler, where StreamJsonRpc has nobody
    /// to report it to and drops it).
    /// </remarks>
    [JsonRpcMethod("textDocument/didChange", UseSingleObjectParameterDeserialization = true)]
    public void DidChange(DidChangeTextDocumentParams p)
    {
        string path = LspConverters.UriToPath(p.TextDocument.Uri);
        string? diverged = null;

        var result = OpenDocumentStore.Change(path, p.TextDocument.Version, original =>
        {
            var text = original;
            foreach (var change in p.ContentChanges)
            {
                if (change.Range is null)
                {
                    text = SourceText.From(change.Text);
                    continue;
                }

                if (LspConverters.TryToTextSpan(text, change.Range) is not { } span)
                {
                    diverged = $"range {change.Range.Start.Line}:{change.Range.Start.Character}"
                        + $"-{change.Range.End.Line}:{change.Range.End.Character} "
                        + $"is outside a {text.Lines.Count}-line buffer";

                    // The batch as a whole, not the part of it that happened to land. The changes
                    // in one notification are a single edit expressed in pieces — each range is
                    // stated against the text the piece before it produced — so half of them is not
                    // a smaller edit, it is a different document. Committing that prefix would hand
                    // the other owner of this entry text no editor has.
                    return original;
                }

                text = text.WithChanges(new TextChange(span, change.Text));
            }
            return text;
        });

        // This session's mirror is provably wrong: a delta landed at offsets its own text cannot
        // hold, so no further delta from this client can be trusted to land either. Ownership is
        // released rather than the text overwritten — divergence is a property of one editor
        // window, and if a second window has the same file open its copy is still good and stays.
        // With no other owner the entry dies and the file reads from disk: wrong until the next
        // save, rather than wrong until the file is closed.
        if (diverged is not null)
        {
            LspLog.Warn($"Dropping '{path}' from this session's open buffers: {diverged}. "
                + "The editor's buffer and this server's copy have diverged; the file will be read "
                + "from disk (or from another window's buffer) until it is saved or reopened.");
            OpenDocumentStore.Close(SessionId, path);
            return;
        }

        // Refused instead: the version did not advance, so this delta was computed against text
        // this server has already moved past. Nothing was applied, which is the whole point — the
        // store still holds the newer text, and dropping the document here would throw that away
        // to punish a notification that arrived late. Reported because a silently ignored edit is
        // its own bug, and this is the only place that can name it.
        if (result is null && OpenDocumentStore.IsOpen(path))
        {
            LspLog.Warn(
                $"Ignored a didChange for '{path}' at version {p.TextDocument.Version}, which does "
                + "not advance the version already held. The notifications for this document "
                + "arrived out of order.",
                key: $"stale-didchange:{path}");
            return;
        }

        if (result is not null && !_clientPullsDiagnostics)
            _diagnostics?.Schedule(path, immediate: false);
        if (result is not null)
        {
            // The edit just invalidated this project's import-completion index; rebuild it once
            // the typing pauses, so the next completion is not served a list that predates it.
            ImportCompletionWarmer.Schedule(path);
            ScheduleClientRefresh();
        }
    }

    [JsonRpcMethod("textDocument/didSave", UseSingleObjectParameterDeserialization = true)]
    public void DidSave(DidSaveTextDocumentParams p)
    {
        string savedPath = LspConverters.UriToPath(p.TextDocument.Uri);
        if (!_clientPullsDiagnostics)
            _diagnostics?.Schedule(savedPath, immediate: true);

        // Saves are when source-generator output is allowed to move: under the default Automatic
        // execution this enqueue is a batched no-op, but it is the signal a Balanced-mode host
        // needs, and wiring it here (with dependent projects bumped by Roslyn) is what makes
        // switching SourceGeneratorExecutionPreference safe at all. Off the dispatch thread —
        // resolving the document can trigger a project load.
        _ = Task.Run(async () =>
        {
            try
            {
                var document = await LspDocumentResolver.ResolveAsync(savedPath, CancellationToken.None);
                document?.Project.Solution.Workspace
                    .EnqueueUpdateSourceGeneratorVersion(document.Project.Id, forceRegeneration: false);
            }
            catch (Exception ex)
            {
                LspLog.Warn($"Source-generator version bump for '{savedPath}' failed: {ex.Message}",
                    key: $"sg-bump:{savedPath}");
            }
        });

        ScheduleClientRefresh();
    }

    // ---- Launch and debug -------------------------------------------------------------

    [JsonRpcMethod("roslynSense/toolchain")]
    public ToolchainInfo Toolchain() => Handlers.LaunchHandler.Toolchain();

    [JsonRpcMethod("roslynSense/debuggerPath")]
    public Task<DebuggerPathResult> DebuggerPath(CancellationToken ct) =>
        Handlers.LaunchHandler.DebuggerPathAsync(ct);

    [JsonRpcMethod("roslynSense/launchTargets", UseSingleObjectParameterDeserialization = true)]
    public Task<LaunchTarget[]> LaunchTargets(LaunchTargetsParams p, CancellationToken ct) =>
        Handlers.LaunchHandler.LaunchTargetsAsync(p, ct);

    [JsonRpcMethod("roslynSense/targetForFile", UseSingleObjectParameterDeserialization = true)]
    public LaunchTarget? TargetForFile(TargetForFileParams p) =>
        Handlers.LaunchHandler.TargetForFile(p);

    [JsonRpcMethod("roslynSense/attachTargets")]
    public AttachTarget[] AttachTargets() => Handlers.LaunchHandler.AttachTargets();

    // ---- Hot reload ---------------------------------------------------------------------

    [JsonRpcMethod("roslynSense/hotReloadStart", UseSingleObjectParameterDeserialization = true)]
    public Task<HotReloadResultDto> HotReloadStart(HotReloadParams p, CancellationToken ct) =>
        Handlers.HotReloadHandler.StartAsync(p, ct);

    [JsonRpcMethod("roslynSense/hotReloadApply", UseSingleObjectParameterDeserialization = true)]
    public Task<HotReloadResultDto> HotReloadApply(HotReloadParams p, CancellationToken ct) =>
        Handlers.HotReloadHandler.ApplyAsync(p, ct);

    [JsonRpcMethod("roslynSense/hotReloadStop", UseSingleObjectParameterDeserialization = true)]
    public HotReloadResultDto HotReloadStop(HotReloadParams p) => Handlers.HotReloadHandler.Stop(p);

    [JsonRpcMethod("roslynSense/hotReloadStatus")]
    public HotReloadStatusDto HotReloadStatus() => Handlers.HotReloadHandler.Status();

    [JsonRpcMethod("roslynSense/hotReloadEnvironment")]
    public HotReloadEnvironmentDto HotReloadEnvironment() => Handlers.HotReloadHandler.Environment();

    // ---- Solution Explorer --------------------------------------------------------------

    [JsonRpcMethod("roslynSense/solutionTree", UseSingleObjectParameterDeserialization = true)]
    public Task<SolutionTreeNode[]> SolutionTree(SolutionTreeParams p, CancellationToken ct) =>
        Handlers.SolutionTreeHandler.ChildrenAsync(p, ct);

    [JsonRpcMethod("roslynSense/solutionProjects")]
    public SolutionProjectInfo[] SolutionProjects() => Handlers.SolutionTreeHandler.Projects();

    [JsonRpcMethod("roslynSense/assemblyReferences", UseSingleObjectParameterDeserialization = true)]
    public string[] AssemblyReferences(SolutionTreeSearchParams p) =>
        Handlers.SolutionTreeHandler.AssemblyReferences(p);

    [JsonRpcMethod("roslynSense/projectTemplates")]
    public Task<ProjectTemplateChoices> ProjectTemplates(CancellationToken ct) =>
        Handlers.SolutionTreeHandler.TemplatesAsync(ct);

    [JsonRpcMethod("roslynSense/solutionTreeSearch", UseSingleObjectParameterDeserialization = true)]
    public Task<SolutionTreeNode[]> SolutionTreeSearch(SolutionTreeSearchParams p, CancellationToken ct) =>
        Handlers.SolutionTreeSearchHandler.SearchAsync(p, ct);

    [JsonRpcMethod("roslynSense/solutionTreeReveal", UseSingleObjectParameterDeserialization = true)]
    public Task<SolutionTreeRevealResult> SolutionTreeReveal(
        SolutionTreeRevealParams p, CancellationToken ct) =>
        Handlers.SolutionTreeSearchHandler.RevealAsync(p, ct);

    [JsonRpcMethod("roslynSense/solutionTreeEdit", UseSingleObjectParameterDeserialization = true)]
    public Task<SolutionTreeEditResult> SolutionTreeEdit(SolutionTreeEditParams p, CancellationToken ct) =>
        Handlers.SolutionTreeEditHandler.EditAsync(p, ct);

    // ---- Virtual documents (generated and decompiled sources) ----------------------------

    [JsonRpcMethod("roslynSense/virtualDocument", UseSingleObjectParameterDeserialization = true)]
    public Task<Handlers.VirtualDocumentResult?> VirtualDocument(
        Handlers.VirtualDocumentParams p, CancellationToken ct) =>
        Handlers.VirtualDocumentHandler.ResolveAsync(p, ct);

    // ---- Packages -----------------------------------------------------------------------

    [JsonRpcMethod("roslynSense/nuget/search", UseSingleObjectParameterDeserialization = true)]
    public Task<Handlers.NuGetSearchResultDto> NuGetSearch(Handlers.NuGetSearchParams p, CancellationToken ct) =>
        Handlers.NuGetHandler.SearchAsync(p, ct);

    [JsonRpcMethod("roslynSense/nuget/versions", UseSingleObjectParameterDeserialization = true)]
    public Task<Handlers.NuGetVersionsResultDto> NuGetVersions(Handlers.NuGetVersionsParams p, CancellationToken ct) =>
        Handlers.NuGetHandler.VersionsAsync(p, ct);

    [JsonRpcMethod("roslynSense/nuget/installed")]
    public Task<Handlers.ProjectPackagesDto[]> NuGetInstalled(CancellationToken ct) =>
        Handlers.NuGetHandler.InstalledAsync(ct);

    [JsonRpcMethod("roslynSense/nuget/updates", UseSingleObjectParameterDeserialization = true)]
    public Task<Handlers.NuGetUpdatesResultDto> NuGetUpdates(Handlers.NuGetUpdatesParams p, CancellationToken ct) =>
        Handlers.NuGetHandler.UpdatesAsync(p, ct);

    [JsonRpcMethod("roslynSense/nuget/packageSources", UseSingleObjectParameterDeserialization = true)]
    public Task<Handlers.NuGetPackageSourcesDto> NuGetPackageSources(
        Handlers.NuGetPackageSourcesParams p, CancellationToken ct) =>
        Handlers.NuGetHandler.PackageSourcesAsync(p, ct);

    [JsonRpcMethod("roslynSense/nuget/consolidations")]
    public Task<Handlers.ConsolidationDto[]> NuGetConsolidations(CancellationToken ct) =>
        Handlers.NuGetHandler.ConsolidationsAsync(ct);

    [JsonRpcMethod("roslynSense/nuget/sources")]
    public Handlers.PackageSourceDto[] NuGetSources() => Handlers.NuGetHandler.Sources();

    [JsonRpcMethod("roslynSense/nuget/sources/edit", UseSingleObjectParameterDeserialization = true)]
    public Handlers.NuGetSourceEditResultDto NuGetEditSources(Handlers.NuGetSourceEditParams p) =>
        Handlers.NuGetHandler.EditSources(p);

    [JsonRpcMethod("roslynSense/nuget/icon", UseSingleObjectParameterDeserialization = true)]
    public Task<Handlers.NuGetIconDto> NuGetIcon(Handlers.NuGetIconParams p, CancellationToken ct) =>
        Handlers.NuGetHandler.IconAsync(p, ct);

    [JsonRpcMethod("roslynSense/nuget/metadata", UseSingleObjectParameterDeserialization = true)]
    public Task<Handlers.PackageMetadataDto?> NuGetMetadata(Handlers.NuGetMetadataParams p, CancellationToken ct) =>
        Handlers.NuGetHandler.MetadataAsync(p, ct);

    [JsonRpcMethod("roslynSense/nuget/checkFramework", UseSingleObjectParameterDeserialization = true)]
    public Task<Handlers.NuGetFrameworkCheckDto> NuGetCheckFramework(
        Handlers.NuGetFrameworkCheckParams p, CancellationToken ct) =>
        Handlers.NuGetHandler.CheckFrameworkAsync(p, ct);

    [JsonRpcMethod("roslynSense/nuget/transitive", UseSingleObjectParameterDeserialization = true)]
    public Handlers.NuGetTransitiveDto NuGetTransitive(Handlers.NuGetTransitiveParams p) =>
        Handlers.NuGetHandler.Transitive(p);

    [JsonRpcMethod("roslynSense/nuget/audit", UseSingleObjectParameterDeserialization = true)]
    public Task<Handlers.NuGetAuditDto> NuGetAudit(Handlers.NuGetAuditParams p, CancellationToken ct) =>
        Handlers.NuGetHandler.AuditAsync(p, ct);

    [JsonRpcMethod("roslynSense/nuget/install", UseSingleObjectParameterDeserialization = true)]
    public Task<Handlers.PackageOperationDto> NuGetInstall(Handlers.NuGetOperationParams p, CancellationToken ct) =>
        Handlers.NuGetHandler.InstallAsync(p, ct);

    [JsonRpcMethod("roslynSense/nuget/update", UseSingleObjectParameterDeserialization = true)]
    public Task<Handlers.PackageOperationDto> NuGetUpdate(Handlers.NuGetOperationParams p, CancellationToken ct) =>
        Handlers.NuGetHandler.InstallAsync(p, ct);

    [JsonRpcMethod("roslynSense/nuget/updatePlan", UseSingleObjectParameterDeserialization = true)]
    public Task<Handlers.NuGetUpdatePlanResultDto> NuGetUpdatePlan(
        Handlers.NuGetUpdatePlanParams p, CancellationToken ct) =>
        Handlers.NuGetHandler.UpdatePlanAsync(p, ct);

    [JsonRpcMethod("roslynSense/nuget/updateAll", UseSingleObjectParameterDeserialization = true)]
    public Task<Handlers.NuGetUpdateAllResultDto> NuGetUpdateAll(
        Handlers.NuGetUpdateAllParams p, CancellationToken ct) =>
        Handlers.NuGetHandler.UpdateAllAsync(p, ct);

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
    public Task<TestRunResponse> TestRun(TestRunParams p, CancellationToken ct) =>
        Handlers.TestHandler.RunAsync(p, ct);

    [JsonRpcMethod("roslynSense/testDebug", UseSingleObjectParameterDeserialization = true)]
    public Task<TestDebugResult> TestDebug(TestDebugParams p, CancellationToken ct) =>
        Handlers.TestHandler.DebugAsync(p, ct);

    [JsonRpcMethod("roslynSense/testCancel", UseSingleObjectParameterDeserialization = true)]
    public void TestCancel(TestCancelParams p) => Handlers.TestHandler.Cancel(p);

    [JsonRpcMethod("roslynSense/testCoverage", UseSingleObjectParameterDeserialization = true)]
    public FileCoverageInfo[] TestCoverage(TestCoverageParams p) =>
        Handlers.TestHandler.Coverage(p);

    [JsonRpcMethod("roslynSense/testsCovering", UseSingleObjectParameterDeserialization = true)]
    public Task<CoveringTestInfo[]> TestsCovering(TestsCoveringParams p, CancellationToken ct) =>
        Handlers.TestHandler.TestsCoveringAsync(p, ct);

    [JsonRpcMethod("roslynSense/coverageSnapshot", UseSingleObjectParameterDeserialization = true)]
    public CoverageSnapshotResult CoverageSnapshot(CoverageSnapshotParams p) =>
        Handlers.TestHandler.CoverageSnapshot(p);

    [JsonRpcMethod("roslynSense/buildCoverageMap", UseSingleObjectParameterDeserialization = true)]
    public Task<BuildCoverageMapResult> BuildCoverageMap(BuildCoverageMapParams p, CancellationToken ct) =>
        Handlers.TestHandler.BuildCoverageMapAsync(p, ct);

    [JsonRpcMethod("roslynSense/impactedTests", UseSingleObjectParameterDeserialization = true)]
    public Task<ImpactedTestsResult> ImpactedTests(ImpactedTestsParams p, CancellationToken ct) =>
        Handlers.TestHandler.ImpactedAsync(p, ct);

    [JsonRpcMethod("roslynSense/changedMembers", UseSingleObjectParameterDeserialization = true)]
    public Task<ChangedMembersResult> ChangedMembers(ChangedMembersParams p, CancellationToken ct) =>
        Handlers.ChangedMembersHandler.GetAsync(p, ct);

    [JsonRpcMethod("workspace/diagnostic", UseSingleObjectParameterDeserialization = true)]
    public Task<WorkspaceDiagnosticReport> WorkspaceDiagnostic(
        WorkspaceDiagnosticParams p, CancellationToken ct) =>
        Guarded(uri: "",
            () => Handlers.WorkspaceDiagnosticsHandler.DiagnoseAsync(p, ct, _languages),
            whenBroken: () => new WorkspaceDiagnosticReport([]));

    /// <summary>
    /// A test seam, not a feature a real editor calls: exposes
    /// <see cref="Services.WorkspaceService.IncrementalLoadCount"/> so an out-of-process test can
    /// assert that a gesture it did not deliberately trigger — a code lens resolving as the user
    /// scrolls, for instance — never silently pulls another project into the workspace.
    /// </summary>
    [JsonRpcMethod("roslynSense/diagnosticsCounters")]
    public DiagnosticsCounters DiagnosticsCounters() => new(Services.WorkspaceService.IncrementalLoadCount);

    [JsonRpcMethod("roslynSense/searchEverywhere", UseSingleObjectParameterDeserialization = true)]
    public Task<SearchEverywhereResult> SearchEverywhere(SearchEverywhereParams p, CancellationToken ct) =>
        Handlers.SearchEverywhereHandler.SearchAsync(p, ct, _languages);

    [JsonRpcMethod("roslynSense/searchText", UseSingleObjectParameterDeserialization = true)]
    public Task<SearchTextResult> SearchText(SearchTextParams p, CancellationToken ct) =>
        Handlers.SearchEverywhereHandler.SearchTextAsync(p, ct);

    [JsonRpcMethod("roslynSense/resolveMetadataTarget", UseSingleObjectParameterDeserialization = true)]
    public Task<ResolveMetadataResult?> ResolveMetadataTarget(ResolveMetadataParams p, CancellationToken ct) =>
        Handlers.SearchEverywhereHandler.ResolveMetadataAsync(p, ct);

    // ---- Settings page ------------------------------------------------------------------

    [JsonRpcMethod("roslynSense/settingChoices", UseSingleObjectParameterDeserialization = true)]
    public SettingChoicesResult SettingChoices(SettingChoicesParams p) =>
        Handlers.SettingsAssistHandler.Choices(p);

    [JsonRpcMethod("roslynSense/memberShape", UseSingleObjectParameterDeserialization = true)]
    public Task<MemberShapeResult> MemberShape(MemberShapeParams p, CancellationToken ct) =>
        Handlers.SettingsAssistHandler.MemberShapeAsync(p, ct);

    [JsonRpcMethod("roslynSense/editorContext", UseSingleObjectParameterDeserialization = true)]
    public void EditorContext(Handlers.EditorContextParams p) =>
        Handlers.EditorContextHandler.Report(p);

    [JsonRpcMethod("workspace/willRenameFiles", UseSingleObjectParameterDeserialization = true)]
    public Task<WorkspaceEdit?> WillRenameFiles(Handlers.RenameFilesParams p, CancellationToken ct) =>
        Handlers.FileOperationsHandler.WillRenameAsync(p, ct, _languages);

    [JsonRpcMethod("workspace/didChangeConfiguration", UseSingleObjectParameterDeserialization = true)]
    public Task DidChangeConfiguration(DidChangeConfigurationParams p, CancellationToken ct) =>
        Handlers.ConfigurationHandler.HandleAsync(p, ct);

    [JsonRpcMethod("workspace/didChangeWatchedFiles", UseSingleObjectParameterDeserialization = true)]
    public void DidChangeWatchedFiles(DidChangeWatchedFilesParams p) =>
        Handlers.WatchedFilesHandler.Handle(p);

    [JsonRpcMethod("workspace/didCreateFiles", UseSingleObjectParameterDeserialization = true)]
    public Task DidCreateFiles(CreateFilesParams p, CancellationToken ct) =>
        Handlers.FileOperationsHandler.DidCreateAsync(p, ct, _languages);

    [JsonRpcMethod("workspace/didDeleteFiles", UseSingleObjectParameterDeserialization = true)]
    public Task DidDeleteFiles(DeleteFilesParams p, CancellationToken ct) =>
        Handlers.FileOperationsHandler.DidDeleteAsync(p, ct, _languages);

    [JsonRpcMethod("textDocument/didClose", UseSingleObjectParameterDeserialization = true)]
    public void DidClose(DidCloseTextDocumentParams p)
    {
        string path = LspConverters.UriToPath(p.TextDocument.Uri);
        OpenDocumentStore.Close(SessionId, path);
        _diagnostics?.Clear(path);
    }

    // ---- Language features -----------------------------------------------------------

    /// <summary>
    /// Sends a request to the pack that owns the document, or to the C# handler when no enabled
    /// pack owns it — or owns it but cannot answer this particular request.
    /// </summary>
    /// <remarks>
    /// Every one of these is a genuine either/or. A markup file has no Roslyn document, so the C#
    /// handlers resolve nothing in it; the pack covers the tags and attributes and hands anything
    /// inside a code block back to Roslyn through its own projection. The one case where both
    /// must run — find-references on a C# symbol also listing the markup that names it — is a
    /// contributor inside the C# handler, not a route.
    /// </remarks>
    private Task<T> Route<TProvider, T>(
        TextDocumentIdentifier textDocument, Func<TProvider, Task<T>> language, Func<Task<T>> csharp,
        Func<T>? whenBroken = null, [CallerMemberName] string method = "")
        where TProvider : class =>
        Route(textDocument.Uri, language, csharp, whenBroken, method);

    /// <summary>The same, for the requests that carry a bare URI: a hierarchy item names its own
    /// document rather than arriving with a <see cref="TextDocumentIdentifier"/>.</summary>
    /// <param name="whenBroken">
    /// What to answer when the request throws. Defaults to an empty array or <see langword="null"/>,
    /// which is the shape of "nothing to report" for nearly every endpoint; pass one where that is
    /// not true — <c>codeLens/resolve</c> has to hand the lens back rather than nothing.
    /// </param>
    /// <remarks>
    /// <para>
    /// One project the tool cannot read must not become an editor that cannot do anything. Without
    /// this boundary any failure — a legacy web project whose <c>$(VSToolsPath)</c> import does not
    /// resolve, a file outside every project, a bug in one pack — travelled out through
    /// StreamJsonRpc as an RPC error, and the client rendered it as "Request codeLens/resolve
    /// failed". On requests the editor re-fires on every scroll that is not one message, it is a
    /// permanent broken state for the whole window, over a problem confined to one project.
    /// </para>
    /// <para>
    /// Degrading to nothing is the honest answer for a question that could not be computed, and it
    /// is what <see cref="Handlers.SolutionTreeHandler"/> already does for the same reason. The
    /// warning is what keeps it from being a silent one — keyed so a broken project says so once
    /// rather than on every scroll.
    /// </para>
    /// <para>
    /// Cancellation is re-thrown rather than swallowed: a cancelled request is the client changing
    /// its mind, and reporting an empty result for one would have the client cache "no lenses here"
    /// for a file that has plenty.
    /// </para>
    /// </remarks>
    private async Task<T> Route<TProvider, T>(
        string uri, Func<TProvider, Task<T>> language, Func<Task<T>> csharp,
        Func<T>? whenBroken = null, [CallerMemberName] string method = "")
        where TProvider : class
    {
        try
        {
            return _languages.Resolve<TProvider>(uri) is { } provider
                ? await language(provider)
                : await csharp();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ReportBroken(method, uri, ex);
            return whenBroken is not null ? whenBroken() : Nothing<T>();
        }
    }

    /// <summary>
    /// Runs an endpoint that does not route through a language pack under the same boundary.
    /// </summary>
    /// <remarks>
    /// Diagnostics are the reason this exists separately. They are answered by one handler for
    /// every language rather than by a pack chosen per file, so they never went through
    /// <see cref="Route{TProvider,T}"/> — and a failure there reached the client as
    /// "Request textDocument/diagnostic failed", repeated for as long as the file stayed open,
    /// while the code lens beside it had already been made to degrade quietly.
    /// </remarks>
    private static async Task<T> Guarded<T>(
        string uri, Func<Task<T>> body, Func<T>? whenBroken = null, [CallerMemberName] string method = "")
    {
        try
        {
            return await body();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ReportBroken(method, uri, ex);
            return whenBroken is not null ? whenBroken() : Nothing<T>();
        }
    }

    /// <summary>Last time each distinct failure was reported, so a repeat is not a flood.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> s_reported = new();

    private static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Says a request failed, once per minute per (endpoint, file, cause).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rate limit is not tidiness. A code lens is re-resolved on every scroll, so a single
    /// unreadable file produced the same line dozens of times in twenty seconds and buried
    /// everything else in the log — including the one other message that would have explained it.
    /// <c>ServiceLog</c>'s key throttles the pop-up but not the log, which is the right default for
    /// a log and the wrong one for something that repeats at scroll frequency.
    /// </para>
    /// <para>
    /// The message carries where it threw, not only what it said. "Value cannot be null. (Parameter
    /// 'filePath')" names no file, no pack and no call — it is a sentence that could have come from
    /// anywhere in the server, and without a frame to go with it the only way to find out is to
    /// guess. The frames are filtered to this assembly because the top of the stack is usually
    /// inside the BCL or Roslyn, and the interesting line is the last one that was ours.
    /// </para>
    /// </remarks>
    private static void ReportBroken(string method, string uri, Exception ex)
    {
        string where = OurFrames(ex);
        string key = $"lsp:{method}:{uri}:{ex.GetType().Name}:{ex.Message}";

        var now = DateTime.UtcNow;
        var last = s_reported.GetOrAdd(key, DateTime.MinValue);
        if (now - last < ReportInterval)
            return;

        s_reported[key] = now;

        ServiceLog.Warn(
            $"{method} failed{Describe(uri)}: {ex.GetType().Name}: {ex.Message}{where}",
            key: key);
    }

    /// <summary>
    /// " for 'Default.aspx'", or nothing when the request did not name one file.
    /// </summary>
    /// <remarks>
    /// Defensive on purpose, and not theoretically. A workspace-wide request carries no URI at all,
    /// and <c>UriToPath</c> builds a <c>Uri</c> — so reporting a failure would itself throw, inside
    /// the catch block whose whole job is to stop exceptions escaping. The result would be the
    /// original error replaced by a confusing one from the error handler, which is the worst
    /// possible outcome for the thing meant to explain what went wrong.
    /// </remarks>
    private static string Describe(string uri)
    {
        if (string.IsNullOrEmpty(uri))
            return "";

        try
        {
            return $" for '{Path.GetFileName(LspConverters.UriToPath(uri))}'";
        }
        catch
        {
            return $" for '{uri}'";
        }
    }

    /// <summary>The first few frames of <paramref name="ex"/> that belong to this assembly.</summary>
    private static string OurFrames(Exception ex)
    {
        string? stack = (ex is AggregateException aggregate
            ? aggregate.InnerException ?? aggregate
            : ex).StackTrace;

        if (stack is null)
            return "";

        // Everything that is not the framework, rather than this assembly only. Filtering to
        // "RoslynMCP." hid the frames that mattered the first time this was used in anger: the
        // throw was inside the markup parser, which lives in a sibling assembly, so the report
        // named the last RoslynMCP frame before it and pointed at a call that was innocent.
        var ours = stack
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("at ", StringComparison.Ordinal))
            .Where(line => !line.StartsWith("at System.", StringComparison.Ordinal)
                        && !line.StartsWith("at Microsoft.", StringComparison.Ordinal))
            .Take(4)
            .ToList();

        return ours.Count == 0 ? "" : $" — {string.Join(" | ", ours)}";
    }

    /// <summary>The empty answer for an endpoint's result type.</summary>
    private static T Nothing<T>() =>
        typeof(T).IsArray
            ? (T)(object)Array.CreateInstance(typeof(T).GetElementType()!, 0)
            : default!;

    [JsonRpcMethod("textDocument/definition", UseSingleObjectParameterDeserialization = true)]
    public Task<Location[]> Definition(TextDocumentPositionParams p, CancellationToken ct) =>
        Route<ILanguageDefinitionProvider, Location[]>(p.TextDocument,
            l => l.DefinitionAsync(p, typeDefinition: false, ct),
            () => Handlers.NavigationHandlers.DefinitionAsync(p, typeDefinition: false, ct, _languages));

    [JsonRpcMethod("textDocument/typeDefinition", UseSingleObjectParameterDeserialization = true)]
    public Task<Location[]> TypeDefinition(TextDocumentPositionParams p, CancellationToken ct) =>
        Route<ILanguageDefinitionProvider, Location[]>(p.TextDocument,
            l => l.DefinitionAsync(p, typeDefinition: true, ct),
            () => Handlers.NavigationHandlers.DefinitionAsync(p, typeDefinition: true, ct, _languages));

    [JsonRpcMethod("textDocument/references", UseSingleObjectParameterDeserialization = true)]
    public Task<Location[]> References(ReferenceParams p, CancellationToken ct) =>
        Route<ILanguageReferencesProvider, Location[]>(p.TextDocument,
            l => l.ReferencesAsync(p, ct),
            () => Handlers.NavigationHandlers.ReferencesAsync(p, ct, _languages));

    [JsonRpcMethod("textDocument/implementation", UseSingleObjectParameterDeserialization = true)]
    public Task<Location[]> Implementation(TextDocumentPositionParams p, CancellationToken ct) =>
        Route<ILanguageImplementationProvider, Location[]>(p.TextDocument,
            l => l.ImplementationAsync(p, ct),
            () => Handlers.NavigationHandlers.ImplementationAsync(p, ct, _languages));

    [JsonRpcMethod("textDocument/hover", UseSingleObjectParameterDeserialization = true)]
    public async Task<Hover?> Hover(TextDocumentPositionParams p, CancellationToken ct)
    {
        // Ahead of the pack rather than instead of it: only an assemblyIdentity's name answers
        // here, and every other position in the same web.config is still the webconfig pack's.
        if (Handlers.BindingRedirectHandler.IsConfigPath(LspConverters.UriToPath(p.TextDocument.Uri)) &&
            await Guarded(p.TextDocument.Uri,
                () => Handlers.BindingRedirectHandler.HoverAsync(p, ct)) is { } redirect)
        {
            return redirect;
        }

        return await Route<ILanguageHoverProvider, Hover?>(p.TextDocument,
            l => l.HoverAsync(p, ct),
            () => Handlers.HoverHandler.HoverAsync(p, ct, _languages));
    }

    [JsonRpcMethod("textDocument/documentHighlight", UseSingleObjectParameterDeserialization = true)]
    public Task<DocumentHighlight[]> DocumentHighlight(TextDocumentPositionParams p, CancellationToken ct) =>
        Route<ILanguageDocumentHighlightProvider, DocumentHighlight[]>(p.TextDocument,
            l => l.DocumentHighlightAsync(p, ct),
            () => Handlers.NavigationHandlers.DocumentHighlightAsync(p, ct));

    [JsonRpcMethod("textDocument/documentSymbol", UseSingleObjectParameterDeserialization = true)]
    public Task<DocumentSymbol[]> DocumentSymbol(DocumentSymbolParams p, CancellationToken ct) =>
        Route<ILanguageDocumentSymbolProvider, DocumentSymbol[]>(p.TextDocument,
            l => l.DocumentSymbolAsync(p, ct),
            () => Handlers.SymbolHandlers.DocumentSymbolsAsync(p, ct));

    [JsonRpcMethod("workspace/symbol", UseSingleObjectParameterDeserialization = true)]
    public Task<SymbolInformation[]> WorkspaceSymbol(WorkspaceSymbolParams p, CancellationToken ct) =>
        Handlers.SymbolHandlers.WorkspaceSymbolsAsync(p, ct, _languages);

    [JsonRpcMethod("textDocument/prepareRename", UseSingleObjectParameterDeserialization = true)]
    public Task<PrepareRenameResult?> PrepareRename(TextDocumentPositionParams p, CancellationToken ct) =>
        Route<ILanguageRenameProvider, PrepareRenameResult?>(p.TextDocument,
            l => l.PrepareRenameAsync(p, ct),
            () => Handlers.RenameHandler.PrepareRenameAsync(p, ct, _languages));

    [JsonRpcMethod("textDocument/rename", UseSingleObjectParameterDeserialization = true)]
    public Task<WorkspaceEdit?> Rename(RenameParams p, CancellationToken ct) =>
        Route<ILanguageRenameProvider, WorkspaceEdit?>(p.TextDocument,
            l => l.RenameAsync(p, ct),
            () => Handlers.RenameHandler.RenameAsync(p, ct, _languages));

    [JsonRpcMethod("textDocument/signatureHelp", UseSingleObjectParameterDeserialization = true)]
    public Task<Protocol.SignatureHelp?> SignatureHelp(SignatureHelpParams p, CancellationToken ct) =>
        Route<ILanguageSignatureHelpProvider, Protocol.SignatureHelp?>(p.TextDocument,
            l => l.SignatureHelpAsync(p, ct),
            () => Handlers.SignatureHelpHandler.SignatureHelpAsync(p, ct));

    [JsonRpcMethod("textDocument/completion", UseSingleObjectParameterDeserialization = true)]
    public Task<CompletionList> Completion(CompletionParams p, CancellationToken ct) =>
        Route<ILanguageCompletionProvider, CompletionList>(p.TextDocument,
            l => l.CompletionAsync(p, _resolveCache, ct),
            () => Handlers.CompletionHandler.CompletionAsync(p, _resolveCache, ct));

    /// <summary>
    /// Resolve carries no document, so it cannot be routed by URI. Items from a pack are
    /// self-contained today; the contract for one that is not is to stamp the pack's id into
    /// <c>data</c> and route on that.
    /// </summary>
    [JsonRpcMethod("completionItem/resolve", UseSingleObjectParameterDeserialization = true)]
    public Task<CompletionItem> CompletionResolve(CompletionItem item, CancellationToken ct) =>
        Handlers.CompletionHandler.ResolveAsync(item, _resolveCache, ct, _languages);

    [JsonRpcMethod("textDocument/codeAction", UseSingleObjectParameterDeserialization = true)]
    public async Task<Protocol.CodeAction[]> CodeAction(CodeActionParams p, CancellationToken ct)
    {
        if (!Handlers.BindingRedirectHandler.IsConfigPath(LspConverters.UriToPath(p.TextDocument.Uri)))
        {
            return await Route<ILanguageCodeActionProvider, Protocol.CodeAction[]>(p.TextDocument,
                l => l.CodeActionsAsync(p, ct),
                () => Handlers.CodeActionHandler.CodeActionsAsync(
                    p, _resolveCache, ct, _clientPicksNestedActions));
        }

        // The redirect fixes first, then whatever the pack that owns the file has to add. The C#
        // handler is skipped rather than routed past: a config file is no Roslyn document, and
        // asking it costs a lookup on every lightbulb to be told so.
        var redirects = await Guarded(p.TextDocument.Uri,
            () => Handlers.BindingRedirectHandler.CodeActionsAsync(p, ct));

        var pack = await Guarded(p.TextDocument.Uri,
            () => _languages.Resolve<ILanguageCodeActionProvider>(p.TextDocument.Uri) is { } provider
                ? provider.CodeActionsAsync(p, ct)
                : Task.FromResult(Array.Empty<Protocol.CodeAction>()));

        return [.. redirects, .. pack];
    }

    [JsonRpcMethod("codeAction/resolve", UseSingleObjectParameterDeserialization = true)]
    public Task<Protocol.CodeAction> CodeActionResolve(Protocol.CodeAction action, CancellationToken ct) =>
        Handlers.CodeActionHandler.ResolveAsync(action, _resolveCache, ct);

    [JsonRpcMethod("textDocument/formatting", UseSingleObjectParameterDeserialization = true)]
    public Task<TextEdit[]> Formatting(DocumentFormattingParams p, CancellationToken ct) =>
        Route<ILanguageFormattingProvider, TextEdit[]>(p.TextDocument,
            l => l.FormatAsync(p, ct),
            () => Handlers.FormattingHandler.FormatAsync(p, ct));

    [JsonRpcMethod("textDocument/rangeFormatting", UseSingleObjectParameterDeserialization = true)]
    public Task<TextEdit[]> RangeFormatting(DocumentRangeFormattingParams p, CancellationToken ct) =>
        Route<ILanguageFormattingProvider, TextEdit[]>(p.TextDocument,
            l => l.FormatRangeAsync(p, ct),
            () => Handlers.FormattingHandler.FormatRangeAsync(p, ct));

    [JsonRpcMethod("textDocument/onTypeFormatting", UseSingleObjectParameterDeserialization = true)]
    public Task<TextEdit[]> OnTypeFormatting(DocumentOnTypeFormattingParams p, CancellationToken ct) =>
        Handlers.FormattingHandler.FormatOnTypeAsync(p, ct);

    [JsonRpcMethod("textDocument/foldingRange", UseSingleObjectParameterDeserialization = true)]
    public Task<Protocol.FoldingRange[]> FoldingRange(FoldingRangeParams p, CancellationToken ct) =>
        Route<ILanguageFoldingRangeProvider, Protocol.FoldingRange[]>(p.TextDocument,
            l => l.FoldingRangeAsync(p, ct),
            () => Handlers.FoldingRangeHandler.FoldingRangesAsync(p, ct));

    [JsonRpcMethod("textDocument/prepareCallHierarchy", UseSingleObjectParameterDeserialization = true)]
    public Task<HierarchyItem[]> PrepareCallHierarchy(TextDocumentPositionParams p, CancellationToken ct) =>
        Route<ILanguageHierarchyProvider, HierarchyItem[]>(p.TextDocument,
            l => l.PrepareCallHierarchyAsync(p, ct),
            () => Handlers.CallHierarchyHandler.PrepareAsync(p, ct));

    [JsonRpcMethod("callHierarchy/incomingCalls", UseSingleObjectParameterDeserialization = true)]
    public Task<CallHierarchyIncomingCall[]> IncomingCalls(CallHierarchyCallsParams p, CancellationToken ct) =>
        Route<ILanguageHierarchyProvider, CallHierarchyIncomingCall[]>(p.Item.Uri,
            l => l.IncomingCallsAsync(p, ct),
            () => Handlers.CallHierarchyHandler.IncomingCallsAsync(p, ct, _languages));

    [JsonRpcMethod("callHierarchy/outgoingCalls", UseSingleObjectParameterDeserialization = true)]
    public Task<CallHierarchyOutgoingCall[]> OutgoingCalls(CallHierarchyCallsParams p, CancellationToken ct) =>
        Route<ILanguageHierarchyProvider, CallHierarchyOutgoingCall[]>(p.Item.Uri,
            l => l.OutgoingCallsAsync(p, ct),
            () => Handlers.CallHierarchyHandler.OutgoingCallsAsync(p, ct));

    [JsonRpcMethod("textDocument/prepareTypeHierarchy", UseSingleObjectParameterDeserialization = true)]
    public Task<HierarchyItem[]> PrepareTypeHierarchy(TextDocumentPositionParams p, CancellationToken ct) =>
        Route<ILanguageHierarchyProvider, HierarchyItem[]>(p.TextDocument,
            l => l.PrepareTypeHierarchyAsync(p, ct),
            () => Handlers.TypeHierarchyHandler.PrepareAsync(p, ct));

    [JsonRpcMethod("typeHierarchy/supertypes", UseSingleObjectParameterDeserialization = true)]
    public Task<HierarchyItem[]> Supertypes(TypeHierarchyItemParams p, CancellationToken ct) =>
        Route<ILanguageHierarchyProvider, HierarchyItem[]>(p.Item.Uri,
            l => l.SupertypesAsync(p, ct),
            () => Handlers.TypeHierarchyHandler.SupertypesAsync(p, ct));

    [JsonRpcMethod("typeHierarchy/subtypes", UseSingleObjectParameterDeserialization = true)]
    public Task<HierarchyItem[]> Subtypes(TypeHierarchyItemParams p, CancellationToken ct) =>
        Route<ILanguageHierarchyProvider, HierarchyItem[]>(p.Item.Uri,
            l => l.SubtypesAsync(p, ct),
            () => Handlers.TypeHierarchyHandler.SubtypesAsync(p, ct));

    [JsonRpcMethod("textDocument/semanticTokens/full", UseSingleObjectParameterDeserialization = true)]
    public Task<SemanticTokens> SemanticTokensFull(SemanticTokensParams p, CancellationToken ct) =>
        Route<ILanguageSemanticTokensProvider, SemanticTokens>(p.TextDocument,
            l => l.SemanticTokensFullAsync(p, _languages, ct),
            () => Handlers.SemanticTokensHandler.SemanticTokensFullAsync(SessionId, p, ct));

    /// <summary>A pack that declines delta answers full instead, which the protocol allows and
    /// clients handle — the same fallback the C# handler takes when it has no baseline.</summary>
    [JsonRpcMethod("textDocument/semanticTokens/full/delta", UseSingleObjectParameterDeserialization = true)]
    public Task<object> SemanticTokensDelta(SemanticTokensDeltaParams p, CancellationToken ct) =>
        Route<ILanguageSemanticTokensProvider, object>(p.TextDocument,
            async l => await l.SemanticTokensFullAsync(new SemanticTokensParams(p.TextDocument), _languages, ct),
            () => Handlers.SemanticTokensHandler.SemanticTokensDeltaAsync(SessionId, p, ct));

    [JsonRpcMethod("textDocument/semanticTokens/range", UseSingleObjectParameterDeserialization = true)]
    public Task<SemanticTokens> SemanticTokensRange(SemanticTokensRangeParams p, CancellationToken ct) =>
        Route<ILanguageSemanticTokensProvider, SemanticTokens>(p.TextDocument,
            l => l.SemanticTokensRangeAsync(p, _languages, ct),
            () => Handlers.SemanticTokensHandler.SemanticTokensRangeAsync(p, ct));

    [JsonRpcMethod("textDocument/selectionRange", UseSingleObjectParameterDeserialization = true)]
    public Task<Protocol.SelectionRange[]> SelectionRange(SelectionRangeParams p, CancellationToken ct) =>
        Route<ILanguageSelectionRangeProvider, Protocol.SelectionRange[]>(p.TextDocument,
            l => l.SelectionRangesAsync(p, ct),
            () => Handlers.SelectionRangeHandler.SelectionRangesAsync(p, ct));

    [JsonRpcMethod("textDocument/linkedEditingRange", UseSingleObjectParameterDeserialization = true)]
    public Task<LinkedEditingRanges?> LinkedEditingRange(TextDocumentPositionParams p, CancellationToken ct) =>
        Route<ILanguageLinkedEditingProvider, LinkedEditingRanges?>(p.TextDocument,
            l => l.LinkedEditingRangesAsync(p, ct),
            () => Handlers.LinkedEditingHandler.RangesAsync(p, ct));

    /// <summary>Advertised only while a pack contributes links, but still routed unconditionally:
    /// a client that cached the capability from an earlier session must get an empty answer rather
    /// than a method-not-found fault.</summary>
    [JsonRpcMethod("textDocument/documentLink", UseSingleObjectParameterDeserialization = true)]
    public Task<DocumentLink[]> DocumentLink(DocumentLinkParams p, CancellationToken ct) =>
        Route<ILanguageDocumentLinkProvider, DocumentLink[]>(p.TextDocument,
            l => l.DocumentLinksAsync(p, ct),
            () => Task.FromResult<DocumentLink[]>([]));

    [JsonRpcMethod("textDocument/inlineValue", UseSingleObjectParameterDeserialization = true)]
    public Task<object[]> InlineValue(InlineValueParams p, CancellationToken ct) =>
        Handlers.InlineValueHandler.InlineValuesAsync(p, ct);

    [JsonRpcMethod("textDocument/diagnostic", UseSingleObjectParameterDeserialization = true)]
    public Task<object> Diagnostic(DocumentDiagnosticParams p, CancellationToken ct) =>
        Guarded<object>(p.TextDocument.Uri,
            () => Handlers.DiagnosticsHandler.PullAsync(p, ct, _languages),
            // A report with no items, rather than null: the client treats the response as a
            // complete answer for this document, and null is not one of the shapes it accepts.
            whenBroken: () => new FullDocumentDiagnosticReport("full", []));

    [JsonRpcMethod("textDocument/codeLens", UseSingleObjectParameterDeserialization = true)]
    public async Task<Protocol.CodeLens[]> CodeLens(CodeLensParams p, CancellationToken ct)
    {
        if (!Handlers.BindingRedirectHandler.IsConfigPath(LspConverters.UriToPath(p.TextDocument.Uri)))
        {
            return await Route<ILanguageCodeLensProvider, Protocol.CodeLens[]>(p.TextDocument,
                l => l.CodeLensAsync(p, ct),
                () => Handlers.CodeLensHandler.CodeLensAsync(p, ct, _languages));
        }

        // Added to the pack's rather than instead of them: a web.config is the webconfig pack's
        // file, and its reference counts have to survive the one lens this contributes above them.
        // The C# handler is skipped for the reason it is skipped in CodeAction — and it matters
        // more here, because the client re-asks for lenses on every scroll.
        var redirects = await Guarded(p.TextDocument.Uri,
            () => Handlers.BindingRedirectHandler.CodeLensAsync(p, ct));

        var pack = await Guarded(p.TextDocument.Uri,
            () => _languages.Resolve<ILanguageCodeLensProvider>(p.TextDocument.Uri) is { } provider
                ? provider.CodeLensAsync(p, ct)
                : Task.FromResult(Array.Empty<Protocol.CodeLens>()));

        return [.. redirects, .. pack];
    }

    /// <summary>Routable where the other resolve endpoints are not: an unresolved lens already
    /// carries the URI it came from.</summary>
    [JsonRpcMethod("codeLens/resolve", UseSingleObjectParameterDeserialization = true)]
    public Task<Protocol.CodeLens> CodeLensResolve(Protocol.CodeLens lens, CancellationToken ct) =>
        Route<ILanguageCodeLensProvider, Protocol.CodeLens>(lens.Data?.Uri ?? "",
            l => CodeLensResolveMemo.ResolveAsync(l, lens, ct),
            () => Handlers.CodeLensHandler.ResolveAsync(lens, ct, _languages),
            // A lens that cannot be counted goes back uncommanded rather than as null, which the
            // client would treat as a protocol violation on top of whatever actually went wrong.
            whenBroken: () => lens);

    [JsonRpcMethod("workspace/executeCommand", UseSingleObjectParameterDeserialization = true)]
    public Task<object> ExecuteCommand(ExecuteCommandParams p, CancellationToken ct) =>
        Handlers.ExecuteCommandHandler.ExecuteAsync(p, ct, _languages);

    [JsonRpcMethod("textDocument/inlayHint", UseSingleObjectParameterDeserialization = true)]
    public Task<InlayHint[]> InlayHint(InlayHintParams p, CancellationToken ct) =>
        Handlers.InlayHintHandler.InlayHintsAsync(p, ct);

    [JsonRpcMethod("roslynSense/onAutoInsert", UseSingleObjectParameterDeserialization = true)]
    public Task<OnAutoInsertResult?> OnAutoInsert(OnAutoInsertParams p, CancellationToken ct) =>
        Route<ILanguageAutoInsertProvider, OnAutoInsertResult?>(p.TextDocument,
            l => l.OnAutoInsertAsync(p, ct),
            () => Handlers.OnAutoInsertHandler.OnAutoInsertAsync(p, ct));

    [JsonRpcMethod("roslynSense/inheritanceMarkers", UseSingleObjectParameterDeserialization = true)]
    public Task<InheritanceMarker[]> InheritanceMarkers(InheritanceMarkersParams p, CancellationToken ct) =>
        Handlers.InheritanceMarkersHandler.MarkersAsync(p, ct);

    [JsonRpcMethod("roslynSense/resolveInheritanceTarget", UseSingleObjectParameterDeserialization = true)]
    public Task<Location?> ResolveInheritanceTarget(ResolveInheritanceTargetParams p, CancellationToken ct) =>
        Handlers.InheritanceMarkersHandler.ResolveTargetAsync(p, ct);

    [JsonRpcMethod("roslynSense/externalConfigReads", UseSingleObjectParameterDeserialization = true)]
    public Task<Location[]> ExternalConfigReads(
        Handlers.ExternalConfigReadsParams p, CancellationToken ct) =>
        Handlers.ExternalConfigReadsHandler.ReadsAsync(p, ct);

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

    /// <summary>
    /// The editor announcing an app it started itself (F5, or Run from the solution explorer).
    /// It goes in the same registry as chat launches, so that a chat asked to "look at the app
    /// I have running" finds it, and so the status bar treats both kinds alike.
    /// </summary>
    [JsonRpcMethod("roslynSense/registerProcess", UseSingleObjectParameterDeserialization = true)]
    public string RegisterProcess(RegisterProcessParams p)
    {
        Services.Run.RunningProcessRegistry.Register(
            EditorSessionId(p.Pid), p.Pid, p.ProjectPath, p.Url, DateTime.UtcNow);
        return EditorSessionId(p.Pid);
    }

    [JsonRpcMethod("roslynSense/unregisterProcess", UseSingleObjectParameterDeserialization = true)]
    public void UnregisterProcess(KillProcessParams p)
    {
        Services.Run.RunningProcessRegistry.Unregister(EditorSessionId(p.Pid));

        // The log outlives the app on purpose — a chat asking why it stopped needs what it
        // printed on the way out. Old ones are swept here rather than on a timer.
        Services.Run.ProcessOutputLog.Sweep();
    }

    /// <summary>
    /// The editor forwarding a launched app's console output, so that GetProjectOutput answers
    /// for the user's apps as well as the chat's own.
    /// </summary>
    [JsonRpcMethod("roslynSense/processOutput", UseSingleObjectParameterDeserialization = true)]
    public void ProcessOutput(ProcessOutputParams p) =>
        Services.Run.ProcessOutputLog.Append(p.Pid, p.Text);

    /// <summary>
    /// Session id for an editor-owned launch. The prefix is load-bearing: it is how both the
    /// status bar and <c>ListRunningProjects</c> tell "the user started this" from "a chat did".
    /// </summary>
    internal static string EditorSessionId(int pid) => $"editor-{pid}";

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
        Handlers.SemanticTokensHandler.Forget(SessionId);
        _diagnostics?.Dispose();
        _refreshDebounce.Cancel();
    }
}

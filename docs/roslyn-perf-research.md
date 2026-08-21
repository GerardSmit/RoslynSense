# Roslyn performance research — synthesis

Findings from reading the Roslyn checkout at `D:\Sources\roslyn` (VS 2026 18.6.3 tag, matching the
referenced Microsoft.CodeAnalysis 5.6.0 packages) against RoslynSense's request paths. Six research
areas produced 24 raw findings; overlapping ones are merged. RoslynSense line references were
spot-checked against the working tree. The already-shipped fix
(`ForceExpandedCompletionIndexCreation` left false plus `ImportCompletionWarmer`) is not repeated.

## Summary

| # | Finding | Area | Impact | Effort | Confidence |
|---|---------|------|--------|--------|------------|
| 1 | Frozen partial semantics is a silent no-op; make it real and extend it to tokens/hover | compilation lifecycle | very high (every keystroke) | hours | high |
| 2 | `SnippetsBehavior = NeverInclude` — snippet providers run for a degraded feature | completion | low but free | minutes | high |
| 3 | Cache compiler diagnostics per document version (bind runs 2-3x per pause) | diagnostics | high | hours | high |
| 4 | documentHighlight via `FindReferencesInDocumentsInCurrentProcessAsync` | navigation | high (fires on caret move) | hours | high |
| 5 | Code lens: `Explicit = false`, unidirectional, capped streaming count | navigation | medium-high | hours | high |
| 6 | `completionList.itemDefaults` + drop per-item `Command` | completion | medium-high (client-side) | ~1 day | high |
| 7 | Find-references: `GetFeatureOptionsForStartingSymbol` (unidirectional cascade) | navigation | medium | hours | high |
| 8 | Use the internal `ShouldTriggerCompletion` overload | completion | low + removes divergence | minutes | high |
| 9 | Start `CompletionSemanticContext` concurrently with `GetCompletionsAsync` | completion | low-medium | hours | medium |
| 10 | `EnqueueUpdateSourceGeneratorVersion` on didSave / after build | source generators | none yet (enables #12) | hours | high |
| 11 | `RefreshDocumentIfStale` write-through instead of per-request fork | compilation lifecycle | medium (MCP sessions) | hours | high |
| 12 | `SourceGeneratorExecutionPreference.Balanced` via host `IWorkspaceConfigurationService` | source generators | high on generator-heavy solutions | days | high |
| 13 | Persistent SQLite index storage (`Solution.FilePath` + storage config) | persistence | high, cold start only | days | high |
| 14 | Warm-up: reorder by open documents, add syntax/symbol-tree indexes | persistence + navigation | high, cold start only | days | medium-high |
| 15 | Search Everywhere on the NavigateTo index instead of `FindSourceDeclarationsAsync` | navigation | high for first search | days | high |
| 16 | Two-pass expanded completion (`NonExpandedItemsOnly` + background merge) | completion | high | days | high |
| 17 | Incremental member-edit analysis (span-limited analyzer pass) | diagnostics | high | days | medium |
| 18 | Bucket analyzers by cost, two waves | diagnostics | medium | days | medium |
| 19 | Rendezvous with the fire-and-forget buffer reconcile | compilation lifecycle | low-medium, every keystroke | days | medium |

---

## 1. Do now

Ranked by expected typing-latency impact.

### 1.1 Frozen partial semantics is a silent no-op — make it real, then extend it

`Document.WithFrozenPartialSemantics` only freezes when `solution.PartialSemanticsEnabled` is true
(`Solution\Document.cs:552-573`). That flag originates at `Workspace.PartialSemanticsEnabled`, a
`protected internal virtual bool => false` (`Workspace.cs:114`), captured into the compilation state
at Solution construction (`Solution.cs:61-66`, `SolutionCompilationState.cs:43,71`) and propagated
through every fork (`SolutionCompilationState.cs:142-149`). No workspace in the checkout overrides
it, and `MSBuildWorkspace` is `sealed` (`MSBuild\Core\MSBuild\MSBuildWorkspace.cs:27`), so it cannot
be overridden by subclassing either. RoslynSense builds a plain `MSBuildWorkspace`
(`Services\WorkspaceService.cs:534`), therefore both freeze calls —
`Lsp\Handlers\CompletionHandler.cs:89` and `Lsp\Handlers\SignatureHelpHandler.cs:39` — return the
same document, and `CompletionService`'s own internal freeze
(`Completion\CompletionService_GetCompletions.cs:71,180-186`) no-ops too. Every completion after a
keystroke therefore drives the tracker to a final compilation with `CreationPolicy.Create`: full
rebind of the edited project plus a source-generator run. The class comment at
`CompletionHandler.cs:19-21` credits a mitigation that is not actually in effect.

**Change.** Prefer the direct call over field surgery: in `CompletionHandler` and
`SignatureHelpHandler`, replace `document.WithFrozenPartialSemantics(ct)` with
`document.Project.Solution.WithFrozenPartialCompilationIncludingSpecificDocument(document.Id, ct)
.GetRequiredDocument(document.Id)` (`Solution.cs:1566-1599`, publicized). It bypasses the gate and
uses the same per-Solution `_documentIdToFrozenSolution` memo (`Solution.cs:43`), so all handlers
freezing the same post-keystroke solution share one frozen compilation. The research's alternative —
patching the `PartialSemanticsEnabled` backing field on `workspace.CurrentSolution.CompilationState`
(`WorkspaceService.cs:839-841`) — is more fragile than it looks: `Solution` re-reads
`workspace.PartialSemanticsEnabled` on every fresh `CreateSolution`, so it must be re-applied after
`OnSolutionAdded` (2.3), `ClearSolution`, and any reload. Its one advantage is also fixing
`CompletionService`'s internal freeze; measure whether that matters before taking the fragility.

Once freezing is real, extend it to the other post-edit consumers, following Roslyn's LSP:
`SemanticTokensHandler.ComputeAsync` (`Lsp\Handlers\SemanticTokensHandler.cs:222-233`, Roslyn does
this at `LanguageServer\Protocol\Handler\SemanticTokens\SemanticTokensHelpers.cs:61`),
`InheritanceMarkersHandler.cs:48` (Roslyn: `InheritanceMargin\AbstractInheritanceMarginService_Helpers.cs:54`),
and `HoverHandler.cs:119`. Freezing also skips generators and skeletons unconditionally —
`ComputeFrozenSnapshotMaps` sets `WithDoNotCreateCreationPolicy` with the explicit comment about not
paying that cost (`SolutionCompilationState.cs:1620-1622`) — so these handlers stop waiting on
generator runs regardless of item 12. `InlayHintHandler.cs:34` is optional: Roslyn does not freeze
inlay hints.

**Expected impact.** Removes full-project rebind plus generator execution from completion, signature
help, semantic tokens, hover and inheritance markers after every edit — tens of ms typical, far more
on generator-heavy or deeply referenced projects. This is exactly the "slow binds starve per-provider
time budgets and collapse the list to locals/keywords" failure `CompletionHandler` documents.

**Risk.** Frozen snapshots are deliberately inaccurate: stale trees for other documents, no generator
re-run, occasional misclassified identifier until the next request. Roslyn ships this trade-off in VS
Code. One real caveat: a project whose tracker never started building freezes to a near-empty
compilation — `WithDoNotCreateCreationPolicy` keeps only documents that already have syntax trees
(`RegularCompilationTracker.cs:778-823`) — so `SolutionWarmup` must stay, and item 14's ordering
becomes more valuable. Do **not** freeze the diagnostics path (see 3.1).

### 1.2 Turn off the new snippet experience

`CompletionOptions.ShouldShowNewSnippetExperience` (`Completion\CompletionOptions.cs:74-91`) falls
back to a feature flag that defaults to true, so `AbstractSnippetCompletionProvider` computes a
syntax context and enumerates `ISnippetService` snippets on every request; `CSharpCompletionService`
maps `SnippetsRule.Default` to `AlwaysInclude` (`Features\CSharp\Portable\Completion\CSharpCompletionService.cs:57-60`)
so `FilterProviders` keeps them all. Microsoft's own LSP server disables it for non-VS clients
(`LanguageServer\Protocol\Handler\Completion\Extensions.cs:36-42`). Worse, RoslynSense's resolve path
never reads `SnippetCompletionItem.LSPSnippetKey` (`CompletionHandler.cs:203-279`), so a committed
semantic snippet loses its placeholders anyway — the cost buys a degraded feature.

**Change.** Add `SnippetsBehavior = SnippetsRule.NeverInclude` to `s_options`
(`CompletionHandler.cs:32-49`). Both members verified present in the referenced 5.6.0 Features
assembly.

**Impact.** Single-digit ms per keystroke, plus removal of a broken commit path.
**Risk.** `for`/`prop`/`ctor` snippet items disappear; VS Code contributes no C# snippets in a bare
LSP setup, so decide deliberately. The alternative is to keep them and implement `LSPSnippetKey` in
resolve.

### 1.3 Cache compiler diagnostics per document version

`SemanticModel.GetDiagnostics()` re-binds every method body on every call and nothing memoizes it.
RoslynSense pays that up to three times per typing pause for the same text: phase 1 at
`Lsp\Handlers\DiagnosticsHandler.cs:111-118`, phase 2 (`+1500ms`) again at `DiagnosticsHandler.cs:72`,
and pull clients again at `DiagnosticsHandler.cs:302` — including the re-pull the analyzer pass
itself requests via `ScheduleRefresh` (`DiagnosticsHandler.cs:352-372`), where only the resultId
marker moved from `c` to `a`. `EmbeddedDiagnosticsAsync` is recomputed each time too
(`DiagnosticsHandler.cs:44,76,314`). Roslyn never recomputes a snapshot twice within a request
(`Diagnostics\Service\DocumentAnalysisExecutor.cs:38-39,217-224`) and keys its pull cache on a
diagnostic version (`DiagnosticAnalyzerService_GetDiagnosticsForSpan.cs:323`). RoslynSense already
has the right key: `Lsp\AnalyzerDiagnosticCache.GetVersionAsync` (textChecksum:dependentSemanticVersion,
`AnalyzerDiagnosticCache.cs:65-75`).

**Change.** A per-document compiler-diagnostics cache keyed identically to `AnalyzerDiagnosticCache`,
with the same LRU/trim discipline, consulted from `CompilerDiagnosticsAsync` in both push phases and
from `PullAsync`. Store embedded-language results in the same entry.
**Impact.** Tens to hundreds of ms per pause on large files, multiplied by open documents when a
refresh triggers re-pulls. **Risk.** Low — the key is precisely the invalidation condition, and it is
the same rule the resultId scheme already relies on.

### 1.4 documentHighlight: stop running the solution-wide reference engine

`NavigationHandlers.DocumentHighlightAsync` (`Lsp\Handlers\NavigationHandlers.cs:390`) calls
`SymbolFinder.FindReferencesAsync(symbol, solution, ImmutableHashSet.Create(document), ct)`. Passing
a document filter to the general engine does not skip `SymbolSet.CreateAsync`'s
`DetermineInitialUpSymbolsAsync` and per-project `InheritanceCascadeAsync`, so a single-document
highlight can force cross-project compilations. Roslyn's highlighter
(`DocumentHighlighting\AbstractDocumentHighlightsService.cs:63-127`) binds with a nullable-disabled
semantic model (line 68), uses `GetFeatureOptionsForStartingSymbol(symbol) with { Explicit = false }`
(line 124) — which also puts the engine on an exclusive serial scheduler
(`FindReferencesSearchEngine.cs:57`) so it never competes with typing — and calls the dedicated
`FindReferencesInDocumentsInCurrentProcessAsync` entry point (line 125; engine at
`FindReferencesSearchEngine_FindReferencesInDocuments.cs:21`).

**Change.** Rewrite `DocumentHighlightAsync` on that pattern, resolving the symbol from a frozen
document. The engine asserts `UnidirectionalHierarchyCascade = true`, so the options change is
mandatory. **Impact.** Highlight fires on every caret move; on a cold or churning solution this turns
a multi-second first highlight into tens of ms and removes background CPU pressure during typing.
**Risk.** Low — unidirectional single-document highlights are what VS shows.

### 1.5 Code lens: serial scheduler, unidirectional, capped

`CodeLensHandler.ResolveReferencesAsync` (`Lsp\Handlers\CodeLensHandler.cs:238`) routes through
`NavigationHandlers.AllReferencesAsync` — parallel, `Explicit = true`, bidirectional cascade,
uncapped — and materializes every location even though only `MaxReferenceLocations` are sent
(line 245). `WebFormsLanguage.CodeLens.cs:135` does the same. Roslyn's counter
(`CodeLens\CodeLensReferencesService.cs:36-83`) uses `Default with { Explicit = false,
UnidirectionalHierarchyCascade = true }` and a streaming progress with a cap that cancels the search
on hit, reporting `cap+`.

**Change.** Give `AllReferencesAsync` an options parameter; for the code-lens caller use the internal
streaming overload (`SymbolFinder_FindReferences_Current.cs:21`) with a capped collector modelled on
`CodeLensFindReferencesProgress`.
**Impact.** Scrolling a file with many lenses stops launching N parallel solution-wide searches
against the typing loop; hot symbols resolve in bounded time. **Risk.** Lenses show `99+` instead of
exact counts; hierarchy-member counts change. The click payload already truncates.

### 1.6 Shrink the completion response: `itemDefaults` and no per-item `Command`

RoslynSense serializes up to 1000 items per keystroke, each with a full `TextEdit` carrying an
identical range (`CompletionHandler.cs:150-153`) and a per-item `Command` whose arguments repeat the
same contextId string (`CompletionHandler.cs:157-160`); the protocol types
(`Lsp\Protocol\Completion.cs:14-47`) have no `itemDefaults` member. Microsoft's server compresses
before serializing: `ItemDefaults.EditRange` once per list, per-item edits omitted when they match,
and `PromoteCommonCommitCharactersOntoList` hoisting commit characters
(`LanguageServer\Protocol\Handler\Completion\CompletionResultFactory.cs:60,88-111,260-306`). Their
list cap is the same 1000 (`LspOptionsStorage.cs:16`).

**Change.** Add `itemDefaults { editRange, data }` guarded by
`textDocument.completion.completionList.itemDefaults`; emit `textEdit` only where insertion text
differs from the label; fold the item identity into `CompletionItemData` and put the contextId in
`itemDefaults.data`. **Impact.** Roughly 50-70% smaller responses on full lists — server
serialization time and, more visibly, VS Code's parse and item-materialization time per keystroke.
**Risk.** Commit behavior: VS Code inserts the label when neither `textEdit` nor `editRange` applies,
so the `InsertionText` special case (committing `List` while displaying `List<>`) needs a per-item
override. The completion-accepted statistics channel must be reworked before deleting `Command`.

### 1.7 Find-references: unidirectional hierarchy cascade

The public `FindReferencesAsync(symbol, solution, ct)` forwards `FindReferencesSearchOptions.Default`
(`SymbolFinder_FindReferences_Legacy.cs:25-36`), whose `UnidirectionalHierarchyCascade` is false, so
`SymbolSet.CreateAsync` builds a bidirectional set (`FindReferencesSearchEngine.SymbolSet.cs:87-89`)
and searches every sibling implementation of every implemented interface member. VS uses
`GetFeatureOptionsForStartingSymbol(symbol)` (`FindReferencesSearchOptions.cs:83-88`).

**Change.** Pass that options value through the publicized internal overload at
`NavigationHandlers.cs:260`, `Tools\FindUsagesTool.cs:78,128`, `Tools\FindTestsTool.cs:184`, and
`Languages\WebForms\Lsp\AspxLanguageHandler.cs:426`.
**Impact.** Multi-x on interface/virtual/override members. **Risk.** Result set changes for hierarchy
members — this is VS's Find All References semantics, but it differs from today's output.
`RenameHandler.cs:69-70` must be explicitly excluded: Roslyn's own doc comment
(`FindReferencesSearchOptions.cs:41-53`) says rename must not use unidirectional cascade.

### 1.8 Use the internal `ShouldTriggerCompletion` overload

The public overload (`Completion\CompletionService.cs:94-110`) begins with
`text.GetOpenDocumentInCurrentContextWithChanges()` and then hardcodes `CompletionOptions.Default
with { ForceExpandedCompletionIndexCreation = true }` rather than the caller's options. RoslynSense's
documents are not in the open-document registry, so at `CompletionHandler.cs:101-103` the reverse
lookup returns null and the veto is decided under different provider filtering than the request that
follows; `GetTriggeredProviders` then repeats the same check internally
(`CompletionService_GetCompletions.cs:131-148`).

**Change.** Call `service.ShouldTriggerCompletion(document.Project, document.Project.Services, text,
offset, trigger, s_options, document.Project.Solution.Options, roles: null)`. **Impact.** Small per
keystroke; more importantly it removes a divergence that becomes real once 1.2 makes the two option
sets differ. **Risk.** Minimal — this is what the public overload wraps.

### 1.9 Overlap `CompletionSemanticContext` with the provider pass

`CompletionHandler.cs:129` awaits `CompletionSemanticContext.CreateAsync` only after
`GetCompletionsAsync` returns, although its only inputs are the frozen document and the span start —
and the span start is computable from text alone via `GetDefaultCompletionListSpan`
(`CompletionService.cs:174-178`, C# override `CSharpCompletionService.cs:42-43`). `CreateAsync` does
a semantic model fetch plus `GetEnclosingSymbol`, a member walk and `LookupSymbols`
(`Lsp\Completion\CompletionSemanticContext.cs:55-86,192-216`), all on the serial tail.

**Change.** Precompute the span start and start `CreateAsync` concurrently with
`GetCompletionsAsync`; fall back to recompute if `completions.Span.Start` differs.
**Impact.** 1-10 ms off the tail of every completion, more right after an edit. **Risk.** Low; the
fallback covers exotic contexts (verbatim identifiers, interpolations). Confidence is medium only
because the span-equality assumption is unverified in practice.

### 1.10 Wire source-generator version bumps now (harmless before item 12)

`Workspace.EnqueueUpdateSourceGeneratorVersion(ProjectId?, bool forceRegeneration)`
(`Workspace\Workspace_SourceGeneration.cs:31-32`) is a batched no-op in Automatic mode unless
`forceRegeneration` is true (guard at `:37-45`), so it can ship ahead of the Balanced switch.
RoslynSense's didSave handler only reschedules diagnostics (`Lsp\LspServer.cs:419-425`) and the repo
has zero references to source-generator execution versions.

**Change.** In `LspServer.DidSave` (line 420), call
`workspace.EnqueueUpdateSourceGeneratorVersion(document.Project.Id, forceRegeneration: false)` —
dependents bump automatically (`Workspace_SourceGeneration.cs:47-135`). Call
`(null, forceRegeneration: true)` after `build_project`, `add_package`, `update_packages`, project
reload and branch-switch storms. Precedents: `DidSaveHandler.cs:32`,
`ProjectSystemProjectFactory.cs:891`. **Impact.** None today; it is the prerequisite that keeps item
12 from serving permanently stale generated code. **Risk.** Low.

### 1.11 `RefreshDocumentIfStale`: write through instead of forking per request

`Services\WorkspaceService.cs:2683-2716` (invoked from `CreateProjectSnapshot` at line 3007 on every
`GetOrOpenProjectAsync`/`FindDocumentAsync` with a target file) calls
`project.Solution.WithDocumentText(...)` at line 2714 and returns a fork that is discarded after the
request. Any semantic question against that fork replays the tree-replace and re-binds
(`CompilationTrackerState.cs:65-98`), and the per-document semantic-model weak reference
(`Document.cs:32,261`) plus the frozen-partial memo (`Solution.cs:43`) are lost with each fork. The
next request repeats it, indefinitely.

**Change.** Under the entry's LoadGate, call `live.OnDocumentTextLoaderChanged(id, new
FileTextLoader(filePath, null))` — the pattern `ReconcileOpenBufferAsync` already uses on the close
path (`WorkspaceService.cs:2070-2071`); Workspace applies it with `PreserveValue`
(`Workspace.cs:1198`). If mutating from a read path is unacceptable, memoize the refreshed Solution
on `CachedWorkspaceEntry` keyed by (DocumentId, content hash), like the overlay memo
(`WorkspaceService.cs:3037-3116`).
**Impact.** MCP tool sessions and any window after external file changes stop re-binding the project
per request — seconds on large projects. **Risk.** Version stamps move once instead of on every
request, which is strictly better; the memoization variant has no behavioral change at all.

---

## 2. Bigger bets

### 2.1 Two-pass expanded completion

VS never puts import completion on the typing path. `CompletionSource`
(`EditorFeatures\Core\IntelliSense\AsyncCompletion\CompletionSource.cs:230-310`) issues two
`GetCompletionsAsync` calls: `NonExpandedItemsOnly`, awaited and shown, and `ExpandedItemsOnly`
started via `Task.Run` and merged on a later refresh. The split is pure provider filtering
(`CompletionService.ProviderManager.cs:180-185`). The expanded providers stay expensive even with
warm caches: `AbstractImportCompletionProvider` computes namespaces in scope via
`GetImportScopes` (lines 58-113), and the extension-member provider resolves candidates against the
receiver type and filters browsability on every dot
(`ExtensionMemberImportCompletionHelper.SymbolComputer.cs:84-127`,
`ExtensionMemberImportCompletionHelper.cs:128-197`) — the caches avoid re-indexing, not per-request
binding. RoslynSense makes one call with the default `AllItems` (`CompletionHandler.cs:112-114`).

**Change.** Await `NonExpandedItemsOnly`; start `ExpandedItemsOnly` as a task memoized on
(documentId, span start, text checksum); give it a 30-75 ms grace window via `Task.WhenAny`; merge
into the next request at the same position otherwise. `isIncomplete` is already true whenever a
prefix exists (`CompletionHandler.cs:167`), so the merge channel exists for free.
**Impact.** Removes the dominant provider cost from dot and identifier completion — tens to a few
hundred ms on first dot over a receiver type in large solutions.
**Risk.** Cross-pass duplicate display names are no longer deduplicated by `DisplayNameToItemsMap`
(`CompletionRanker` can dedup on display text plus provenance); expanded items can arrive one
keystroke late — VS's documented trade-off; memoization must invalidate on document version.
**Prerequisite.** None strictly, but do it after 1.1 so the measurement is not dominated by binds.

### 2.2 `SourceGeneratorExecutionPreference.Balanced`

`WorkspaceConfigurationOptions` (`Workspace\IWorkspaceConfigurationService.cs:35-45`) has exactly two
members in 5.6.0; the MEF default service returns `Automatic` (`:20-23`), while VS and the Roslyn LSP
servers export a Host-layer override reading a global option that defaults to Balanced
(`LanguageServer\Protocol\Features\Options\WorkspaceConfigurationService.cs:13-25`,
`WorkspaceConfigurationOptionsStorage.cs:19-25`). A plain `MSBuildWorkspace` gets Automatic. Under
non-Automatic, the tracker downgrades to `GeneratedDocumentCreationPolicy.CreateOnlyRequired`
(`RegularCompilationTracker.cs:611-628`) and later edit forks reattach previously generated trees
instead of running the driver (`RegularCompilationTracker_Generators.cs:37-50,475-503`).

**Change.** Export `[ExportWorkspaceService(typeof(IWorkspaceConfigurationService),
ServiceLayer.Host), Shared]` from the RoslynMCP assembly (already in the composition —
`WorkspaceService.cs:304-308`) returning `new WorkspaceConfigurationOptions(SourceGeneratorExecution:
Balanced)`. Pass nothing else: `ValidateCompilationTrackerStates` is debug-only in Roslyn's own build
and the historical knobs (`DisableRecoverableText`, `CacheStorage`) no longer exist in 5.6.0 — do not
port older advice, it will not compile.
**Impact.** Removes the generator run from the 400 ms compiler-diagnostics pass, analyzer phase, code
lens and every MCP overlay fork — tens of ms typically, hundreds to seconds with heavy generators.
Cold start unchanged. **Risk.** Generated documents are stale between saves — VS's shipped
trade-off — and `get_source_generated_file_content` / `list_source_generated_files` will serve stale
text unless they enqueue a bump first. **Prerequisite.** Item 1.10 must land first, or generated code
never refreshes. Note items 1.1 and 2.2 are complementary, not redundant: freezing protects the
handlers that freeze, Balanced protects the ones that must not (diagnostics).

### 2.3 Persistent index storage

`GetPersistentStorageService` returns `SQLitePersistentStorageService` whenever it is in the
composition (`Storage\PersistentStorageExtensions.cs:15-24`), and
`DefaultPersistentStorageConfiguration` supplies a real cache folder for any host
(`Workspace\Host\PersistentStorage\IPersistentStorageConfiguration.cs:31-98`). The single hard gate
is `AbstractPersistentStorageService.GetStorageAsync:45-46` — `solutionKey.FilePath == null` returns
NoOp. `MSBuildWorkspace` sets `Solution.FilePath` only in `OpenSolutionAsync` via `OnSolutionAdded`
(`MSBuildWorkspace.cs:216`); `OpenProjectAsync` never does. RoslynSense loads projects individually
(`WorkspaceService.cs:551`, `Lsp\SolutionWarmup.cs:107-144`) and has zero `OnSolutionAdded` calls, so
every index — `SyntaxTreeIndex`, `TopLevelSyntaxTreeIndex`, `SymbolTreeInfo` — is rebuilt each daemon
start. `SQLitePCLRaw.bundle_e_sqlite3` is already referenced (`RoslynMCP.csproj:61`).

**Change.** Call `workspace.OnSolutionAdded(SolutionInfo.Create(SolutionId.CreateNewId(),
VersionStamp.Create(), filePath: BoundSolutionPath))` immediately after `MSBuildWorkspace.Create` and
before the first `OpenProjectAsync`, guarded to the primary cached workspace only. Pair it with a
Host-layer `IPersistentStorageConfiguration` returning a version-independent folder: the default keys
the cache on `Process.MainModule.FileName` (`IPersistentStorageConfiguration.cs:44-55,79-91`), which
changes on every dotnet-tool version bump and would orphan the cache each upgrade. Keep that folder
out of `%TEMP%`.
**Impact.** Second and later daemon sessions: first find-references, first Search Everywhere, and the
extension-method half of first import completion become checksum-keyed reads. Type-import completion
is memory-only (`AbstractTypeImportCompletionService.cs:27-28`), so `ImportCompletionWarmer` stays
exactly as is. Zero effect on the per-keystroke loop — do not expect one, and do not route hot-path
reads through storage. **Risk.** One storage instance owns the DB exclusively via a `FileShare.None`
lock (`SQLite\v2\SQLitePersistentStorage.cs:120-168`); a second workspace given the same FilePath
degrades to NoOp silently. All failure paths fall back to recompute, so the downside is wasted
effort, not corruption. **Interaction.** `OnSolutionAdded` constructs a fresh `Solution`; if the
backing-field variant of 1.1 is ever chosen, it must be applied after this call.

### 2.4 Warm-up: order by relevance, warm the indexes that gestures actually block on

Three findings converge here. (a) `SolutionWarmup.WarmSymbolsAsync`
(`Lsp\SolutionWarmup.cs:174-203`) iterates `solution.Projects` in solution order sequentially, so the
project the user has files open in is warmed whenever the loop reaches it — which matters much more
once 1.1 lands, because freezing a never-built project yields a near-empty compilation
(`RegularCompilationTracker.cs:778-823`). Tracker state is advanced in place and shared by reference
across forks, so warm-up work is never wasted. (b) The warm's `FindSourceDeclarationsAsync` builds
only the top-level index (`Project.cs:419-486`); find-references narrows through the *other* index
(`Finders\AbstractReferenceFinder.cs:128,339`) and go-to-implementation builds `SymbolTreeInfo` per
metadata reference on first use (`DependentTypeFinder.cs:341-346`). (c) Once storage exists, the
index sweep needs only text checksums, not compilations
(`FindSymbols\Shared\AbstractSyntaxIndex.cs:80-113`), so it should run *before* the compilation loop.

**Change.** Order projects containing `OpenDocumentStore` paths first; yield between projects so a
keystroke burst is not starved; optionally warm 2-3 projects concurrently under a semaphore. Add a
throttled `SyntaxTreeIndex.GetIndexAsync` sweep and `SymbolTreeInfo.GetInfoForMetadataReferenceAsync`
per distinct PE reference. After 2.3 lands, move the index sweep ahead of the compilation loop.
**Impact.** `SolutionWarmup`'s own comment (`SolutionWarmup.cs:153-157`) measures 7.3 s cold vs
0.15 s warm first Ctrl+T on an 18-project/2,600-document solution; index enumeration is ~8 ms once
indexes exist. First Shift+F12 / Ctrl+F12 stops paying index construction inside the user's wait.
**Risk.** More background CPU and memory in the minutes after connect — throttle. On a first-ever
session the reorder front-loads parse-and-walk before compilations, roughly cost-neutral.
**Prerequisite.** The reorder in (c) only pays off after 2.3.

### 2.5 Search Everywhere on the NavigateTo index

`SymbolFinder.FindSourceDeclarationsAsync` loops projects sequentially and, for each name match,
calls `GetRequiredCompilationAsync` plus `GetSymbolsWithName`
(`SymbolFinder_Declarations_CustomQueries.cs:56-67,103-110`) — every compilation alive, real symbols
for every hit. Roslyn's NavigateTo pattern-matches `DeclaredSymbolInfo` records straight out of the
per-document index, in parallel, streaming, with no compilation dependency
(`NavigateTo\AbstractNavigateToSearchService.NormalSearch.cs:53-95`). `SearchEverywhere.FindSymbolsAsync`
(`Lsp\Search\SearchEverywhere.cs:178`) uses the former, and `SymbolHandlers.cs:175` blocks on the
warm because of it.

**Change.** Run the existing `IdentifierMatcher` and tiering over `DeclaredSymbolInfo.Name` from
`TopLevelSyntaxTreeIndex.GetIndexAsync` per document, or call
`SearchFullSolutionInCurrentProcessAsync` and map `RoslynNavigateToItem` into `SearchHit`.
**Impact.** Cold search drops from ~7 s to parse/index time, and to near-zero with 2.3; the feature
works before `SolutionWarmup` finishes instead of waiting on it.
**Risk.** `DeclaredSymbolInfo` is syntax-derived: no `ISymbol` for exotic filters, container strings
formatted differently than `ToDisplayString()`. Moderate rewrite of one method plus its tests.

### 2.6 Incremental member-edit analysis

Roslyn's typing-loop optimization is
`DiagnosticAnalyzerService.IncrementalMemberEditAnalyzer.cs:54-130`: `IDocumentDifferenceService.
GetChangedMemberAsync` diffs snapshots (lines 190-225), and when exactly one method-level member
changed, the compiler and every span-capable analyzer run with `span = changedMember.FullSpan`, the
compiler's span widened by `GetAdjustedSpanForCompilerAnalyzerAsync`
(`DocumentAnalysisExecutor.cs:267-300`). Span capability comes from `DiagnosticAnalyzerCategory`;
unknown third-party analyzers get worst-case document analysis
(`Diagnostics\DiagnosticAnalyzerExtensions.cs:9-47`). RoslynSense always analyzes whole files:
`filterSpan` is explicitly null at `Services\AnalyzerService.cs:148`, and the compiler pass has no
span (`DiagnosticsHandler.cs:117`). `AnalyzerDiagnosticCache.TryGetPrevious:107-110` already keeps
the last full result.

**Change.** Keep a weak reference to the last-analyzed `Document` per `DocumentId`; on the next pass
call `GetChangedMemberAsync`; on a single-member edit run the span-capable subset with the member's
`FullSpan` and splice fresh findings into the previous cached result, re-mapping prior spans by the
text delta. Fall back to full-file otherwise.
**Impact.** The dominant cost of the 1.5 s analyzer phase drops to one member's span for the most
common edit shape — the difference between the analyzer budget tripping
(`AnalyzerService.cs:156-167`) and finishing comfortably on multi-thousand-line files.
**Risk.** Medium: splicing and span remapping are fiddly, and CS8019/unused-usings are never reported
under a span (`DocumentAnalysisExecutor.cs:349-351`), so those ids must be merged from prior
whole-file results. Signature-changing edits must not take the incremental path —
`GetChangedMemberAsync` already returns null for them. **Prerequisite.** Item 1.3's cache.

### 2.7 Bucket analyzers by cost

Roslyn de-prioritizes analyzers registering SymbolStart/End or SemanticModel actions, detected via
the public `GetAnalyzerTelemetryInfoAsync` and cached in a `ConditionalWeakTable`
(`DiagnosticAnalyzerService_DeprioritizationCandidates.cs:86-106`, gate at
`DiagnosticAnalyzerService_GetDiagnosticsForSpan.cs:241-263`); the compiler analyzer is never
de-prioritized. RoslynSense runs the whole set against one budget (`AnalyzerService.cs:75-83,137-167`);
when the semantic half exceeds it, everything but syntax results is discarded and `Failed = true`
blocks caching (`AnalyzerDiagnosticCache.cs:222-223`), so one pathological analyzer starves every
cheap one, repeatedly.

**Change.** Classify once per analyzer after `DriverFor` (`AnalyzerService.cs:239-250`), then run two
`GetAnalyzerSemanticDiagnosticsAsync` calls with analyzer subsets: fast bucket with the normal budget
(publish immediately), slow bucket with its own budget merged in when it lands.
**Impact.** Cheap code-style squiggles appear reliably at the 1.9 s mark even with an expensive
third-party analyzer present. **Risk.** Two invocations repeat compilation-event generation for the
tree; the second wave needs its own cancellation so a keystroke kills both. Misclassification affects
only ordering.

### 2.8 Rendezvous with the buffer reconcile

didChange (`Lsp\LspServer.cs:343-417`) updates `OpenDocumentStore` synchronously and queues
`ReconcileOpenBufferAsync` via `Task.Run` (`WorkspaceService.cs:1963`). A request arriving before the
reconcile lands builds an overlay fork off the stale base (`WorkspaceService.cs:3037-3116`) and a
frozen compilation on it; the reconcile then mutates `CurrentSolution` (line 2054) and that work is
garbage. Frozen results and semantic models are cached per Solution instance (`Solution.cs:43`,
`Document.cs:32`), so each lineage pays its own build.

**Change.** Record the in-flight reconcile task per file and have `LspDocumentResolver.ResolveAsync` /
`CreateProjectSnapshot` await it with a small bound (~25-50 ms, for the load-gate-contended case at
`WorkspaceService.cs:2009-2016`), skipping the wait when nothing is pending.
**Impact.** One duplicate frozen build per keystroke removed, and completion → signature help →
semantic tokens for one version share a frozen snapshot. Modest per event, paid every keystroke.
**Risk.** Adds up to the bound in worst-case latency under a project load; correctness still covered
by the overlay. **Prerequisite.** Only meaningful after 1.1.

---

## 3. Rejected / not worth acting on

- **Freeze the diagnostics path.** Deliberately not done in Roslyn — no handler under
  `LanguageServer\Protocol\Handler\Diagnostics` freezes. Frozen-partial produces false errors from
  not-yet-parsed siblings, which would be published as squiggles. Keep `DiagnosticsHandler` on the
  real solution and let 2.2 remove its generator cost instead.
- **Rework `CompilationWithAnalyzers` caching.** `AnalyzerService.DriverFor`
  (`AnalyzerService.cs:235-250`) keys one driver per Compilation in a `ConditionalWeakTable` with
  analyzer-set validation — the same granularity and lifetime as Roslyn's per-Project cache
  (`DiagnosticAnalyzerService_CompilationWithAnalyzersPair.cs:34-38,45-72`). Already at parity.
- **Set `concurrentAnalysis: false`.** Roslyn does this only because its host mixes async with
  synchronous blocking and starves threads (`CompilationWithAnalyzersPair.cs:103-105`); the rationale
  does not apply to a pure-async daemon.
- **Other workspace-configuration knobs.** `DisableRecoverableText`, `CacheStorage`,
  `DisableSharedSyntaxTrees` and friends do not exist in 5.6.0 —
  `WorkspaceConfigurationOptions` has exactly two members. `ValidateCompilationTrackerStates` is
  false in release builds and must stay that way.
- **`UpdateCurrentSolutionWithStaleSourceGeneratorDocuments`.** No such API in 5.6.0;
  `EnqueueUpdateSourceGeneratorVersion` is the equivalent. Listed only so the name is not chased.
- **Change rename options.** `RenameHandler.cs:69-70` already passes the cheapest correct
  `SymbolRenameOptions`; the remaining cost is inherent conflict resolution. The only action is the
  negative one in 1.7 — do not let unidirectional cascade leak into rename.
- **Normalize `PreserveIdentity` to `PreserveValue`.** `ReconcileOpenBufferAsync`
  (`WorkspaceService.cs:2054`) is correct as written: open buffers are bounded, and `PreserveValue`
  would add temp-storage serialization per keystroke and break the reference-equality short-circuits
  at `WorkspaceService.cs:3080-3096`. Do not "fix" this in a cleanup pass.
- **Add extension-method-specific warming.** Redundant after 2.3 —
  `SymbolTreeInfo` persists the receiver-type-to-extension-member map
  (`SymbolTreeInfo_Serialization.cs:131-151`), so first use becomes a DB read.
- **Expect persistence to help typing.** It cannot: completion, semantic models and analyzer runs
  never consult storage, and in-memory `ConditionalWeakTable` caches answer first for open documents
  (`AbstractSyntaxIndex.cs:21-22,56-77`). Treat 2.3 as cold-start-only and verify no regression.

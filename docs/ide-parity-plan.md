# RoslynSense IDE-parity plan (Tiers 1–3)

Goal: make RoslynSense a credible replacement for `ms-dotnettools.csharp` + C# Dev Kit for
day-to-day C# work in VS Code, on par with Rider/Visual Studio for the core loop —
**edit → see analyzer feedback → run tests → debug → navigate**.

This plan covers the three tiers identified in the gap audit:

| Tier | Theme | Outcome |
| --- | --- | --- |
| 1 | Blockers | The extension becomes a daily driver; C# Dev Kit can be uninstalled |
| 2 | Parity | Feature set stops feeling thinner than Dev Kit |
| 3 | Debugger depth | Debugging feels like Rider, both for the user and for the AI mirror |

Tier 4 (Razor/Blazor/XAML/WinForms designers, Source Link, code-style settings UI) is
deliberately out of scope here. WebForms has since outgrown it entirely — `.aspx` and its siblings
are a first-class LSP language now, not an MCP-only side surface. Razor's server-side services are
still MCP-only, and deliberately so — see [Gaps against VS and Rider](#gaps-against-vs-and-rider).

---

## 0. Ground rules

1. **The editor talks LSP, not MCP.** MCP tools are the AI surface; the extension must never
   shell out to `roslyn-sense --cli`. Anything the extension needs gets a custom
   `roslynSense/*` LSP method served by the shared daemon, so both surfaces observe the same
   workspace, the same open-buffer overlay, and the same caches.
2. **Extract services, don't duplicate them.** Several capabilities already exist but are
   trapped inside MCP tool classes that return markdown (`DiscoverTestsTool`,
   `RunTestsTool.FormatTrxOutput`, `RunConfigResolver`). Each of those gets lifted into a
   service returning structured records; the MCP tool keeps its markdown formatting by calling
   the service. No behavior change on the MCP side, which keeps the existing MCP tests honest.
3. **Never block the typing loop.** Anything expensive (analyzers, reference counts,
   workspace diagnostics) runs off the keystroke path, is cached against
   `Project.GetDependentSemanticVersionAsync` + document checksum, and reports staleness by
   asking the client to refresh rather than by holding a request open.
4. **Degrade, don't fail.** Every new capability must have a defined behavior when the
   underlying tool is missing (no netcoredbg, no `dotnet` on PATH, .NET Framework target),
   surfaced through `window/showMessage` — never a silent no-op and never a crash.
5. **Split the extension.** `vscode-extension/src/extension.ts` is already 1631 lines. Every
   new feature lands in its own module (`src/<feature>.ts`) exporting a single
   `register<Feature>(context, client)` and is called from `activate()`. Existing code stays
   put; no big-bang refactor.

### Verified facts this plan depends on

- `Microsoft.CodeAnalysis.CSharp.Features` 5.6.0 is already referenced and publicized
  (`RoslynMCP.csproj:23`, `:47-48`) — IDE analyzers and EnC APIs are reachable.
- netcoredbg supports `--interpreter=vscode` (native DAP). Its `initialize` response reports:
  `supportsSetVariable`, `supportsSetExpression`, `supportsConditionalBreakpoints`,
  `supportsFunctionBreakpoints`, `supportsExceptionInfoRequest`, `supportsExceptionFilterOptions`,
  `supportsTerminateRequest`, `supportTerminateDebuggee`, `supportsCancelRequest`, and exception
  filters `all` / `user-unhandled`. It does **not** report hit-condition breakpoints, logpoints,
  data breakpoints, `evaluateForHovers`, or delayed stack-trace loading.
- `DebuggerService.FindOrProvisionNetcoredbgAsync` (`DebuggerService.cs:486-526`) already probes
  the tools cache, `PATH`, well-known install dirs, and finally downloads from the Samsung
  release feed. The extension can reuse this rather than shipping its own binary.
- `AppRunService.StartAsync` deliberately launches the built apphost rather than `dotnet run`
  so the PID is stable and attachable (`RunConfigResolver.cs:127-128`) — the launch-debug flow
  is a short hop from what exists.
- The extension currently registers **no** `createFileSystemWatcher` and no
  `clientOptions.synchronize.fileEvents`, confirming the watched-files gap.

---

# Contents

- [0. Ground rules](#0-ground-rules) · [Verified facts this plan depends on](#verified-facts-this-plan-depends-on)
- [**Status**](#status) — [Tier 1](#tier-1-status) · [Tier 2](#tier-2-status) · [Tier 3](#tier-3-status) · [Tier M](#tier-m-status) · [Beyond the plan](#beyond-the-plan) · [Implementation notes](#implementation-notes)
- [**What is still missing**](#what-is-still-missing) — [Debugger surface asymmetries](#debugger-surface-asymmetries) · [Gaps against VS and Rider](#gaps-against-vs-and-rider) · [Deliberately out of scope](#deliberately-out-of-scope)
- [Tier 1 — Blockers](#tier-1--blockers)
  - [T1.1 Analyzer diagnostics in the LSP surface](#t11-analyzer-diagnostics-in-the-lsp-surface)
  - [T1.2 Real debugging (F5) without ms-dotnettools.csharp](#t12-real-debugging-f5-without-ms-dotnettoolscsharp)
  - [T1.3 Test Explorer](#t13-test-explorer)
  - [T1.4 `workspace/didChangeWatchedFiles`](#t14-workspacedidchangewatchedfiles)
- [Tier 2 — Parity](#tier-2--parity)
  - [T2.1 Solution Explorer (Rider-grade)](#t21-solution-explorer-rider-grade)
  - [T2.2 NuGet management (WebView, Rider-style)](#t22-nuget-management-webview-rider-style)
  - [T2.3 Progress reporting (`$/progress`)](#t23-progress-reporting-progress)
  - [T2.4 Refresh fan-out for lenses and hints](#t24-refresh-fan-out-for-lenses-and-hints)
  - [T2.5 Build tasks and problem matcher](#t25-build-tasks-and-problem-matcher)
  - [T2.6 Workspace-wide diagnostics](#t26-workspace-wide-diagnostics)
  - [T2.7 File rename → symbol and namespace fixups](#t27-file-rename--symbol-and-namespace-fixups)
  - [T2.8 Completion snippets](#t28-completion-snippets) · [T2.9 Semantic token modifiers](#t29-semantic-token-modifiers) · [T2.10 User-visible server messages](#t210-user-visible-server-messages)
  - [T2.11 Multi-root and multiple solutions](#t211-multi-root-and-multiple-solutions)
  - [T2.12 Metadata as source](#t212-metadata-as-source)
- [Tier 3 — Debugger depth](#tier-3--debugger-depth)
  - [T3.1 Structured debug data in the backend](#t31-structured-debug-data-in-the-backend)
  - [T3.2 AI mirror adapter fidelity](#t32-ai-mirror-adapter-fidelity)
  - [T3.3 Hit counts and logpoints](#t33-hit-counts-and-logpoints)
  - [T3.4 Data breakpoints — implemented by emulation](#t34-data-breakpoints--implemented-by-emulation)
  - [T3.5 .NET Framework native debugging](#t35-net-framework-native-debugging)
  - [T3.6 Hot Reload — implemented as real Edit-and-Continue](#t36-hot-reload--implemented-as-real-edit-and-continue)
- [Tier M — MCP surface](#tier-m--mcp-surface)
  - [TM.1 Analyzer diagnostics on the MCP path](#tm1-analyzer-diagnostics-on-the-mcp-path-rides-t11) · [TM.2 Project and package mutation](#tm2-project-and-package-mutation) · [TM.3 Editor context](#tm3-editor-context)
  - [TM.4 Structured tests and formatting](#tm4-structured-tests-and-formatting) · [TM.5 Debugger tools](#tm5-debugger-tools-for-the-structured-backend-rides-t31) · [TM.6 Symmetry checklist](#tm6-symmetry-checklist)
- [Sequencing](#sequencing) · [Cross-cutting acceptance test](#cross-cutting-acceptance-test)

---

# Status

Every numbered item in this plan is implemented as designed. What follows records **how** each one
landed, and what shipped beyond it.

## Tier 1 status

| Item | State | Delivered as |
| --- | --- | --- |
| T1.1 analyzer diagnostics | Done | `AnalyzerService.RunDocumentAnalyzersAsync`, `AnalyzerDiagnosticCache`, two-phase `DiagnosticsPublisher`, IDE analyzers reflected out of the Features assemblies, and Roslyn's `IConfigurationFixProvider` exports for suppress/configure — with reserved lightbulb slots so they cannot be crowded out |
| T1.2 native F5 | Done | `LaunchHandler` (`launchTargets`, `attachTargets`, `debuggerPath`), `debugLaunch.ts`; netcoredbg `--interpreter=vscode` for CoreCLR, `roslyn-sense --dap` for Framework |
| T1.3 Test Explorer | Done | `TestDiscoveryService`/`TrxParser`/`TestRunService` + `TestHandler` + `testController.ts`, with run, debug and coverage profiles; `roslynSense/testRunEvent` reports each test as it finishes and streams console output, `roslynSense/testCancel` kills the test host, and coverage carries branch counts |
| T1.4 watched files | Done | `WatchedFilesHandler` with 500 ms coalescing, rename pairing, project/`.editorconfig` eviction; `synchronize.fileEvents` on the client. `workspace/didCreateFiles` scaffolds a file made through the editor's own explorer; `didDeleteFiles` drops it from its project |

## Tier 2 status

| Item | State | Delivered as |
| --- | --- | --- |
| T2.1 Solution Explorer | Done | `SolutionFileService`, `ProjectEvaluationService`, `FileNestingService`, three tree handlers, `solutionExplorer.ts` — solution folders, Dependencies subtree, show-all-files, filter, go-to-node, reveal, drag-and-drop, add/delete/rename, cut/copy/paste/duplicate, `Alt+Insert` new item, `F5` set-as-startup-and-debug, package details, and a nesting toggle with user-defined rules |
| T2.2 NuGet panel | Done | `NuGetFeedContext` (NuGet.config chain, source mapping, per-feed outcomes, feed add/remove/reorder/enable, credential providers with an editor sign-in prompt) + `NuGetMetadataService` (README, license, deprecation, vulnerabilities, dependency groups), `PackageUpdateService` (version locks and batch update), `PackageAuditService`, `PackageFrameworkService`, `NuGetIconService`/`NuGetPayloadService`, `ProjectAssetsService`; Central Package Management resolves in `ProjectEvaluationService.ReadPackages`. Panel split into `src/nuget/` and a type-checked `src/webview/nuget/`. `PackagesConfigService` covers legacy projects |
| T2.3 `$/progress` | Done | `LspProgress` + `ProgressReporter`, on solution load, restore, reload, debugger provisioning, test runs |
| T2.4 refresh fan-out | Done | `RefreshKind` flags through `ScheduleClientRefresh`; diagnostics, code lens and inlay hints all re-request |
| T2.5 build tasks | Done | `taskProvider.ts` + `$msCompile`; build errors reach Problems through T1.2's launch path |
| T2.6 workspace diagnostics | Done | `WorkspaceDiagnosticsHandler`, scoped `off`/`openProjects`/`solution`, analyzers read cache-only |
| T2.7 file rename fixups | Done | `FileOperationsHandler` on `workspace/willRenameFiles` — renames the matching type and its references |
| T2.8–T2.10 snippets, token modifiers, messages | Done | `InsertTextFormat` with real placeholders; ten semantic token modifiers; `LspLog` fanning `logMessage`/`showMessage` |
| — settings | Done | `analyzerDiagnostics`, `codeStyleDiagnostics`, `analyzerTimeoutSeconds`, `workspaceDiagnostics`, `sourceLink`, `solutionExplorer.fileNesting` and `fileNesting.rules` are contributed to VS Code and applied through `workspace/didChangeConfiguration` without a restart |
| T2.11 multi-root | Done | Solution bound per workspace folder, one client per solution, binding follows the focused editor |
| T2.12 metadata as source | Done | One `roslynSense/virtualDocument` resolver behind `roslynsense-generated:` and `roslynsense-metadata:`; `LspDocumentResolver` accepts both so hover and navigation work inside them |

## Tier 3 status

| Item | State | Delivered as |
| --- | --- | --- |
| T3.1 structured debug data | Done | `StackFrameInfo`/`VariableInfo`/`ThreadInfo`, lazy child expansion, both engines |
| T3.2 AI mirror fidelity | Done | Real stacks, variable trees, pause, terminate, `setVariable`, exception filters and `exceptionInfo` |
| T3.3 hit counts and logpoints | Done | Emulated in `PublishingDebugBackend`; reaches the MCP tools, the AI mirror and the Framework DAP — **not** the netcoredbg session |
| T3.4 data breakpoints | Done | Step-and-compare in `DataBreakpointWatcher`, both runtimes, `write` access only, one statement late |
| T3.5 .NET Framework debugging | Done | `roslyn-sense --dap` over the ICorDebug backend; the extension selects it whenever the target is Framework |
| T3.6 Hot Reload | Done | CoreCLR in-process via `RoslynMCP.HotReloadAgent`; Framework via `ICorDebugModule2::ApplyChanges` from a break state. Each proven end to end against a live process |
| — run to cursor, set next statement, modules, detach | Done | Widened `IDebugEngine`; `DebugNavigationTool` for the AI, `gotoTargets`/`goto` in the Framework DAP. **Not surfaced in the AI mirror adapter** |
| — Framework exception settings | Done | `ExceptionFilters` honored by the ICorDebug backend; the Framework DAP advertises the `all` filter |

## Tier M status

| Item | State | Delivered as |
| --- | --- | --- |
| TM.1 analyzer diagnostics on MCP | Done | `AnalyzerOptions` fix, project-scope analyzers, `get_solution_diagnostics` |
| TM.2 project and package mutation | Done | `ProjectMutationService` + `NuGetService`; packages, references, add/delete file, create project, add to solution |
| TM.3 editor context | Done | `EditorContextStore`, `roslynSense/editorContext`, `get_editor_context`, prompt-hook injection |
| TM.4 structured tests and formatting | Done | `get_test_failures` over a recorded run history, `format_document`, `rename_file` |
| TM.5 debugger tools | Done | `debug_pause`, `debug_select_frame`, `debug_expand`, `debug_set_variable`, plus the navigation tools above |

## Beyond the plan

Shipped since the plan was written, and not tracked by any item in it:

| Capability | Delivered as |
| --- | --- |
| .NET Framework across the whole tool surface | `NetFxToolchain`, `ReferenceAssemblyRedirector`, legacy project mutation, `packages.config` management, Framework test runs |
| Classic ASP.NET F5 under IIS Express | Launch targets of kind `aspnetClassic`, plus WebForms `.aspx` source mapping |
| Change Signature and Move Type to File | `RefactoringService`, exposed through `RefactorTool` |
| Relevance-ranked completion | `RoslynMCP/Lsp/Completion/` — ordering by match quality rather than alphabetically |
| Per-build daemon keying | `HostPaths` salts the solution key with the server assembly's MVID, so a rebuilt server never reuses a stale daemon |
| Profiling, memory and database tooling | `ProfileTool`, `MemoryTool`, `DatabaseTool` and their services — an MCP-only surface with no editor counterpart |
| Language packs | `Languages/` — a language beyond C# is one folder owning one engine and both front-ends, so an editor feature and the matching MCP tool cannot drift apart. `Languages/WebForms/` is the worked example; `Languages/Razor/` is MCP-only by choice. See [`RoslynMCP/Languages/README.md`](../RoslynMCP/Languages/README.md) |
| WebForms as a first-class editor language | Navigation, references, rename across markup and code-behind, completion, diagnostics in and out of the editor, outline and folding, call and type hierarchy, workspace symbols, linked editing, document links, selection ranges, semantic tokens, auto-insert, event-handler generation, and breakpoints in markup — see the [WebForms section](../vscode-extension/README.md#webforms) of the extension README |
| Embedded languages in C# strings | `Languages/Abstractions/EmbeddedStringLanguage.cs` and `RoslynEmbeddedLanguages` — a language claims `[StringSyntax]` identifiers and Roslyn's own `EmbeddedLanguageDetector` does the resolution, including `// lang=` comments. A language Roslyn's detector cannot be made to name implements `IConfiguredStringLanguage` and answers `Detect` itself, as one fallback in `DetectAtAsync`. Orthogonal to packs: a route template owns a span inside someone else's document, not a file |
| Resource keys as a navigable thing | `Languages/Resources/` — `.resx` is a pack, and a resource key is navigable from C#, from `<%$ Resources: … %>` / `<%$ dnnLoc: … %>` and from `meta:resourcekey`. The unit is the *family* (every `.resx` sharing a base name in a directory), because which one wins at runtime depends on a portal, a thread culture and a database-stored fallback locale, none of which exists in an editor — so definition, hover, completion and rename answer with the family rather than a guess. Which call shapes carry a key is configurable (`resources.preset` / `lookups`), and confidence gates the features: rename is refused where the resource file could only be inferred from proximity. See [`RoslynMCP/Languages/README.md`](../RoslynMCP/Languages/README.md#resources-the-one-pack-whose-model-needs-explaining) |
| Source Link | `SourceLinkService` reads the Source Link map out of a dependency's portable PDB, resolves the declaring document, downloads it, and verifies it against the checksum the PDB recorded before go-to-definition lands in it. Decompilation is the fallback for every failure — no PDB, no map, unreachable host, checksum mismatch |
| Inline debug values | `textDocument/inlineValue` — locals, parameters and field accesses in the stopped frame's own method, up to the stopped line. The server says *what* to resolve and the client resolves it against whichever session is stopped, so it works on all three debug surfaces without any of them knowing |
| Expand selection, linked editing | `textDocument/selectionRange` walks the syntax tree outward; `textDocument/linkedEditingRange` covers locals, parameters, range variables, labels and method type parameters — the symbols whose every reference is provably in the file |
| Semantic tokens on large files | `semanticTokens/range` for what is on screen, and `semanticTokens/full/delta` against a per-session baseline, so an edit sends the changed span rather than every token in the file |

## Implementation notes

Kept because they change what the remaining work can assume:

- `AnalyzerService.RunAnalyzersAsync` now takes a nullable `filePath` (null = whole project) and
  passes `project.AnalyzerOptions`. Both were bugs: project-scope requests ran no analyzers, and
  `.editorconfig` severities were ignored on every path.
- Roslyn's IDE analyzers are loaded reflectively from the Features assemblies
  (`AnalyzerService.LoadIdeAnalyzers`). A Roslyn upgrade that moves those types fails
  `IdeAnalyzersLoadFromFeaturesAssemblies` rather than silently dropping every IDE0xxx rule.
- `ExecuteCommandHandler.ExecuteAsync` returns `object`, not `string` — `roslynSense.build`
  returns a structured `BuildResult`.
- `WorkspaceService.EvictProjectAsync` is public API now (was a test-only hook).
- Test discovery resolves attributes semantically. Writing a `FactAttribute` into a project's own
  namespace shadows Xunit's for that whole namespace — which is correct behavior, and worth
  remembering when adding fixtures.

---

# What is still missing

Every numbered item in the plan is now implemented as designed. What remains is in two groups:
where the three debug surfaces disagree with each other, and where the product is behind Visual
Studio and Rider on ground the plan never covered.

> **The second group is now measured rather than estimated.** A full audit catalogued 1,093
> Rider/Visual Studio capabilities and checked every one against this repository: 362 are at parity,
> 736 are open, and five are defects rather than gaps. The section below is the summary this plan
> shipped with and is kept for history; it undercounts badly.
>
> - [ide-gap-analysis.md](ide-gap-analysis.md) — the verdict, the five defects, the platform
>   question, the root causes and the fifteen highest-value small fixes
> - [ide-gap-register.md](ide-gap-register.md) — all 736 open items with file-level evidence
> - [ide-capability-catalog.md](ide-capability-catalog.md) — what Rider and VS actually provide

## Debugger surface asymmetries

Three DAP surfaces exist and they are not equivalent. This is the single largest source of "it
works for the AI but not for me", so it is worth stating flatly.

| Capability | Native F5, CoreCLR (netcoredbg) | Native F5, Framework (`--dap`) | AI mirror adapter |
| --- | --- | --- | --- |
| Conditional breakpoints | yes | yes | yes |
| Hit-count breakpoints | **no** | yes (emulated) | yes (emulated) |
| Logpoints | **no** | yes (emulated) | yes (emulated) |
| Data breakpoints | **no** | yes (emulated) | yes (emulated) |
| Run to cursor / set next statement | **no** | yes | **no** |
| Modules view | **no** | **no** (engine has it; no DAP `modules` request) | **no** |
| Exception filters | `all`, `user-unhandled` | `all` only | `all`, `user-unhandled` |
| Hot Reload | yes, and without a debugger at all | yes, from a break state only | via the MCP tools |
| `setVariable`, `evaluate`, variable trees | yes | yes | yes |

The netcoredbg column is not ours to fix: those rows are absent from its `initialize` response and
the emulation in `PublishingDebugBackend` sits behind our own backend, which that session does not
use. Closing it means either an adapter of our own in front of netcoredbg, or routing the CoreCLR
F5 session through `PublishingDebugBackend` the way the Framework one already is. The other two
columns are ours: `modules` and `gotoTargets` in the AI adapter are small, contained additions.

## Gaps against VS and Rider

Ground the plan never covered, ordered by how often it would be noticed.

| Gap | Detail |
| --- | --- |
| **Razor and Blazor get no editor features of ours.** `Languages/Razor/` implements go-to-definition, rename, outline and diagnostics for `.razor` and `.cshtml`, but registers no LSP providers, so opening one in VS Code falls through to the C# handlers and gets nothing markup-aware. That is a decision rather than missing wiring: Razor's editor support comes from the C# Dev Kit's own Razor server, and the pack exists to give an AI session the tool surface it would otherwise lack. The seam is in place if the decision changes — implementing the provider interfaces on `RazorLanguage` is all it takes, and nothing in `LspServer` changes. Note the pack has no find-usages handler; `find_usages` on a `.razor` answers from Roslyn alone |
| **Which branch ran is unknown.** Cobertura records how many of a line's branches were taken, not which. The coverage view therefore reports "1 of 2" correctly but cannot colour the arms; naming one would be inventing detail the format does not carry |
| **Source Link resolves by name, not by token.** There is no public map from an `ISymbol` to a metadata token, so a method is matched by name and every overload resolves to the same declaration line. They are always in one file, so navigation lands correctly; the line can be a sibling overload's |
| **A resource key can be found but not created.** Everything in `Languages/Resources/` reads: `ResxReader` produces spans for entries that exist, and every edit the pack emits — including a family-wide rename — is a `TextEdit` over one of those spans. There is no writer, so "move string literal to resource" and any other code action that would add a `<data name=…>` are not implemented, and the missing-key diagnostic points at a key you have to go and write yourself. The insertion side is the real work: the entry belongs in the family's neutral file, which may be neither the file the caret is in nor a file that exists yet |

### What WebForms still does not do

This entry has been a gap twice — first "WebForms is MCP-only", then "WebForms answers in the
editor but Razor does not" — and it is neither now. `.aspx` and its siblings answer navigation,
references, rename across both halves, completion, diagnostics with the file closed, outline and
folding, call and type hierarchy, workspace symbols, linked editing, document links, selection
ranges, semantic tokens, auto-insert, event-handler generation, and breakpoints in the gutter. What
is left is short, and most of it is a decision rather than a backlog.

**Markup formatting is deferred, not skipped.** `textDocument/formatting` and `rangeFormatting` on
an `.aspx` currently do nothing — `WebFormsLanguage` does not implement
`ILanguageFormattingProvider`, so the request falls through to the C# handler, which has no Roslyn
document for a markup file and returns no edits. The interface exists and the shape of the work is
known; it was pushed behind everything else because it is the largest single item in the pack and
the one users would notice least, and because a formatter that reflows markup wrongly is worse than
none. Anyone picking it up should treat attribute wrapping, embedded `<% %>` blocks, and the
`<script>`/`<style>` islands as three separate problems.

**`inlayHint` is skipped by choice.** Markup states its types: `<asp:Label ID="lblTotal">` names
both the type and the identifier on the same line. There is nothing to infer that the text does not
already say, so a hint would be redundant chrome rather than information.

**`onTypeFormatting` is skipped by choice.** `roslynSense/onAutoInsert` covers what actually helps —
`>` closing a tag, `<%--` completing its comment — without reflowing the document on every
keystroke. C# keeps its `;` and `}` triggers; markup gets nothing, deliberately.

**`inlineValue` returns nothing for markup, by construction.** It is advertised server-wide because
C# needs it, but markup has no debug-time locals of its own — anything a `<% %>` block touches is a
member of the page class and is already covered on the C# side.

**One inconsistency is accepted.** The `codeLens` reference count over a C# member comes from
Roslyn alone, while `textDocument/references` on the same member also lists the markup that names
it. So a handler method can read "2 references" and produce three results. Making the two agree
means running the markup contributor once per lens, which is far too expensive for something that
renders above every member in the file. The lens under-counts; the reference list is the truth.

## Deliberately out of scope

Unchanged from the original Tier 4 decision, and still not planned: XAML and WinForms visual
designers, a code-style settings UI, and anything that would need a design surface rather than a
text buffer. Designer *regeneration* is supported for `.aspx` and `.dbml`
(`Services/Designers/`) — that is code generation, not a designer.

---

# Tier 1 — Blockers

## T1.1 Analyzer diagnostics in the LSP surface

### Problem

`DiagnosticsHandler.ComputeAsync` (`RoslynMCP/Lsp/Handlers/DiagnosticsHandler.cs:12-26`) calls
only `SemanticModel.GetDiagnostics()`. `AnalyzerService.RunAnalyzersAsync` exists and works, but
is reachable only from `GetRoslynDiagnosticsTool` — and only on its single-file path. Net effect:
StyleCop, Roslynator, in-house analyzers, and every `IDE0xxx` code-style rule produce zero
squiggles in the editor while reporting correctly to the AI. `CodeActionHandler.cs:33-60` has the
same blind spot, so analyzer-driven quick fixes are missing from the lightbulb too.

Two secondary defects in the existing analyzer path:

- `compilation.WithAnalyzers(analyzers)` (`AnalyzerService.cs:82`) is called **without**
  `project.AnalyzerOptions`, so `.editorconfig` / `.globalconfig` severity overrides and analyzer
  configuration are ignored entirely.
- It runs whole-compilation `GetAnalyzerDiagnosticsAsync()` and then filters by file path — cost
  is O(project) for a single document's squiggles. Unusable on a keystroke path.

### Design

**A per-document analyzer path, cached, off the typing loop, merged into one publish.**

1. **New API — `AnalyzerService.RunDocumentAnalyzersAsync(Document document, CancellationToken ct)`**
   returning `ImmutableArray<Diagnostic>`:
   - Load analyzers via the existing `LoadAnalyzersForProject` (keeps the `AnalyzerHost` ALC,
     shadow-copy, and rebuild-eviction machinery untouched).
   - Build `CompilationWithAnalyzers` with
     `new CompilationWithAnalyzersOptions(project.AnalyzerOptions, onAnalyzerException: log-and-continue,
     concurrentAnalysis: true, logAnalyzerExecutionTime: false, reportSuppressedDiagnostics: false)`.
     Passing `project.AnalyzerOptions` is what makes `.editorconfig` severities apply.
   - Use the **per-tree** APIs — `GetAnalyzerSyntaxDiagnosticsAsync(tree, …)` +
     `GetAnalyzerSemanticDiagnosticsAsync(semanticModel, filterSpan: null, …)` — instead of the
     whole-compilation call. This is the difference between "usable" and "not".
   - Hard time budget (`AnalyzerTimeout`, default 15s, setting-overridable) via a linked CTS;
     on timeout return what completed and log through `window/logMessage`.
   - Fix `RunAnalyzersAsync` to pass `project.AnalyzerOptions` as well, so the MCP tool benefits
     from `.editorconfig` severities too.

2. **New IDE-analyzer source.** Third-party analyzers come from `project.AnalyzerReferences`, but
   `IDE0xxx` code-style diagnostics live inside the Features assemblies, which are referenced but
   never instantiated as analyzers. Add
   `AnalyzerService.LoadIdeAnalyzers()`: reflect over `Microsoft.CodeAnalysis.CSharp.Features` and
   `Microsoft.CodeAnalysis.Features` for exported non-abstract `DiagnosticAnalyzer` types carrying
   `[DiagnosticAnalyzer(LanguageNames.CSharp)]`, skipping the compiler diagnostic analyzer
   (`*CompilerDiagnosticAnalyzer`, which would duplicate compiler diagnostics) and any
   `IBuiltInAnalyzer` that requires host services we don't provide. Cache the resulting
   `ImmutableArray` statically. Gate behind `roslynSense.codeStyleDiagnostics` (default `true`)
   because these are the rules most likely to be noisy on an unconfigured repo.
   *Risk:* internal Features types are version-sensitive. Wrap the whole load in try/catch, log
   once on failure, and fall back to third-party analyzers only. Add a unit test asserting the
   loader returns a non-empty set for the pinned Roslyn version so an upgrade breaks the build,
   not the user.

3. **New cache — `RoslynMCP/Lsp/AnalyzerDiagnosticCache.cs`.**
   Key: `(DocumentId, textChecksum, dependentSemanticVersion)`. Value: the analyzer diagnostic
   array. Bounded LRU (default 64 entries) so a large solution can't grow it unbounded. Exposes:
   - `TryGet(document, out diagnostics)` — synchronous, no compute.
   - `GetOrComputeAsync(document, ct)` — computes and stores.
   The key deliberately mirrors the existing pull `resultId` scheme in
   `DiagnosticsHandler.PullAsync:31-49`, so both can share one version string.

4. **Two-phase publish in `DiagnosticsPublisher`.**
   The current publisher (400 ms debounce, one compute, one publish) becomes:
   - *Phase 1* at 400 ms: compiler diagnostics, published immediately. Unchanged latency.
   - *Phase 2* at 1500 ms of idle (separate CTS, `AnalyzerDebounce`): analyzer diagnostics from
     the cache, then republish **compiler ∪ analyzer** for that document.
   Because `textDocument/publishDiagnostics` replaces the whole set per URI, the publisher must
   retain the last compiler set per file (`ConcurrentDictionary<string, Diagnostic[]>`) so phase 2
   can union rather than clobber. Phase 2 is skipped entirely when phase 1's document version is
   already stale.

5. **Pull path.** `DiagnosticsHandler.PullAsync` returns compiler ∪ *cached* analyzer diagnostics.
   On cache miss it returns compiler-only immediately, kicks off the analyzer compute in the
   background, and on completion calls the (generalized) client-refresh from T2.4 so the client
   re-pulls and gets the complete set. Never block a pull on analyzers.

6. **Code actions.** `CodeActionHandler` takes its range diagnostics from
   compiler ∪ `AnalyzerDiagnosticCache.TryGet(...)` (cache-only — no compute inside a lightbulb
   request). Dedupe on `(Id, Location.SourceSpan)` exactly as
   `GetRoslynDiagnosticsTool.CollectFileDiagnosticsAsync:172` does. Raise the per-range diagnostic
   cap from 10 to 25 since analyzer diagnostics will now compete for those slots, and keep
   `MaxActions = 25`.

7. **Suppression fixes.** With analyzer diagnostics flowing, Roslyn's suppression providers become
   relevant. Register `IConfigurationFixProvider`/suppression actions in the code-action catalog so
   "Suppress `SA1600` in `.editorconfig` / with `#pragma`" appears — matching Rider's behavior and
   giving users an escape hatch for noisy analyzers on first run.

### Files

- `RoslynMCP/Services/AnalyzerService.cs` — new per-document API, `AnalyzerOptions` fix, IDE analyzer loader.
- `RoslynMCP/Lsp/AnalyzerDiagnosticCache.cs` — new.
- `RoslynMCP/Lsp/DiagnosticsPublisher.cs` — two-phase publish, retained compiler set.
- `RoslynMCP/Lsp/Handlers/DiagnosticsHandler.cs` — merge on compute and pull.
- `RoslynMCP/Lsp/Handlers/CodeActionHandler.cs` — merged diagnostic source, suppression fixes.
- `RoslynMCP/Config/EffectiveSettings.cs` — `codeStyleDiagnostics`, `analyzerDiagnostics`, `analyzerTimeoutSeconds`.

### Tests

- `AnalyzerDiagnosticsTests`: fixture project with a NuGet analyzer producing a known ID → LSP
  compute returns it; `.editorconfig` downgrading it to `suggestion` changes the reported severity
  (this is the regression test for the `AnalyzerOptions` fix); setting `none` removes it.
- Cache test: two computes at the same semantic version hit the cache once (counter on the
  compute delegate); an edit bumps the key and recomputes.
- `CodeActionHandler` offers a fix for an analyzer-sourced diagnostic.
- IDE analyzer loader returns a non-empty set and includes at least one `IDE0` rule.
- Publisher test: two publishes per edit, second is a superset of the first.

### Definition of done

Open the sandbox with StyleCop or Roslynator installed: squiggles appear within ~1.5 s of stopping
typing, `.editorconfig` severity changes take effect on reload, the lightbulb offers analyzer fixes
plus suppression, and typing latency for compiler squiggles is unchanged from today.

---

## T1.2 Real debugging (F5) without ms-dotnettools.csharp

### Problem

The only contributed debugger is `roslynsense-ai` — attach-only, and by design a *mirror* of an
AI-owned session. There is no way to launch and debug the user's own app. The user has the MS C#
extensions disabled, so F5 currently does nothing useful.

### Design

**Contribute a second, real debugger type backed by netcoredbg's native DAP mode.** We write no
adapter code: `--interpreter=vscode` gives VS Code a fully-featured DAP server, so watch windows,
locals trees, conditional breakpoints, exception settings, and `setVariable` all come free.

1. **Server: expose the debugger binary.**
   New LSP method `roslynSense/debuggerPath` → `{ path: string, provisioned: bool }`, implemented by
   calling the existing `DebuggerService.FindOrProvisionNetcoredbgAsync`. Because provisioning can
   download ~15 MB, wrap it in a `$/progress` report (T2.3) so the user sees "Downloading .NET
   debugger…" rather than a hang. Failure returns a structured error which the extension surfaces
   with an actionable message (install netcoredbg, or set `roslynSense.debuggerPath`).

2. **Server: expose launch targets.**
   New LSP method `roslynSense/launchTargets` → array of
   `{ projectPath, projectName, targetFramework, executable, arguments[], workingDirectory,
   environment{}, kind: "console"|"web"|"aspnetClassic"|"test"|"library", launchProfiles[],
   isNetFramework, applicationUrl? }`.
   Implementation is a thin handler over the existing `RunConfigResolver` +
   `MsBuildLocator.GetTargetPath` + `launchSettings.json` parsing that `RunProjectTool` already
   uses. Library and unsupported projects are returned with `kind: "library"` so the picker can
   filter rather than guess.

3. **Server: build-before-launch.**
   New `workspace/executeCommand` command `roslynSense.build` taking `{ projectPath, configuration }`
   and returning `{ success, errors[], warnings[] }` sourced from the existing build service. Errors
   are returned structured so the extension can push them to a diagnostic collection instead of
   dumping text.

4. **Extension: `src/debugLaunch.ts`.**
   - `contributes.debuggers` gains type `roslynsense`, label "C# (RoslynSense)", `languages: ["csharp"]`,
     with `launch` attributes (`program`, `args`, `cwd`, `env`, `stopAtEntry`, `justMyCode`,
     `console`, `projectPath`, `configuration`, `launchProfile`, `serverReadyAction`) and `attach`
     attributes (`processId`, `processName`).
   - `registerDebugAdapterDescriptorFactory('roslynsense', …)` returning
     `new vscode.DebugAdapterExecutable(pathFromServer, ['--interpreter=vscode'])`.
   - `registerDebugConfigurationProvider('roslynsense', …)`:
     - `provideDebugConfigurations` → one generated config per non-library launch target, so
       "Add Configuration…" produces something correct.
     - `resolveDebugConfiguration` → when invoked with an empty config (F5 with no `launch.json`),
       pick the launch target: single candidate wins, otherwise a QuickPick remembered in
       workspace state. Then fill `program`/`args`/`cwd`/`env` from the target and the selected
       `launchSettings.json` profile (including `ASPNETCORE_URLS` and `ASPNETCORE_ENVIRONMENT`).
     - `resolveDebugConfigurationWithSubstitutedVariables` → run `roslynSense.build` and abort the
       session with a clear message if the build failed. This replaces `preLaunchTask` so F5 works
       with no `tasks.json` at all; the task provider from T2.5 remains available for users who
       want explicit tasks.
     - `.NET Framework` targets: netcoredbg cannot debug them. Detect via the launch target's
       `isNetFramework` and show a message pointing at the AI-side ICorDebug path, rather than
       failing inside the adapter. (A future `RoslynMCP.Debugger`-backed DAP server is the real fix;
       tracked in T3.5.)
   - `serverReadyAction`: for `kind: "web"`, default to opening the browser on the
     `Now listening on: {url}` pattern, matching Dev Kit's behavior.
   - **Breakpoint forwarding must not fight the real session.** `registerBreakpointForwarding`
     currently skips forwarding while `activeDebugSession?.type === 'roslynsense-ai'`. Extend the
     skip to `'roslynsense'`: when the user is debugging their own app, VS Code owns breakpoints and
     the adapter handles them; we still push the snapshot to `roslynSense/syncBreakpoints` so the
     shared set (and any AI session started later) stays correct.

5. **Attach flow.** `roslynsense` attach reuses the existing `RunningProcessRegistry`/
   `list_running_projects` data through a new `roslynSense/attachTargets` method, so "Attach to
   process" lists .NET processes with project names rather than a raw PID list.

### Files

- `RoslynMCP/Lsp/Handlers/LaunchTargetsHandler.cs`, `DebuggerPathHandler.cs` — new.
- `RoslynMCP/Lsp/Handlers/ExecuteCommandHandler.cs` — `roslynSense.build`.
- `RoslynMCP/Lsp/Protocol/Launch.cs` — new DTOs.
- `RoslynMCP/Lsp/LspServer.cs` — method registration.
- `vscode-extension/src/debugLaunch.ts` — new; `package.json` debugger contribution.
- `vscode-extension/src/extension.ts` — one `registerDebugLaunch(context)` call; breakpoint-forwarding guard.

### Tests

- `LaunchTargetsHandlerTests` against fixture solutions: console app resolves to the apphost with
  the right cwd; web app reports `kind: "web"` and its `applicationUrl`; a library reports
  `kind: "library"`; a `net48` project reports `isNetFramework: true`.
- `DebuggerPathHandlerTests`: returns the cached path when present without touching the network.
- Manual E2E in `D:\Sources\roslyn-sandbox`: F5 with no `launch.json` builds and starts the WebApi
  project, a gutter breakpoint in `OrderCalculator.Total` hits, locals/watch/`setVariable` work,
  and stopping the session leaves the daemon and any AI session alive.

### Definition of done

With every ms-dotnettools extension disabled: F5 launches and debugs the sandbox web app, hits
breakpoints, evaluates in the Debug Console, edits a variable in the Variables view, and breaks on
a thrown exception when the `all` filter is enabled.

---

## T1.3 Test Explorer

### Problem

No `TestController` exists. The only test affordance is a CodeLens command that opens a terminal
and runs `dotnet test --filter …` (`extension.ts:325-338`). Test discovery, TRX parsing, and
Cobertura coverage all exist server-side but are collapsed to markdown inside MCP tool methods
before anything else can consume them.

### Design

**Lift test logic into services, expose structured LSP methods, drive `vscode.tests` from them.**

1. **Extract services** (no behavior change to MCP output):
   - `RoslynMCP/Services/Testing/TestDiscoveryService.cs` — the Roslyn walk currently inside
     `DiscoverTestsTool` (which builds a private `TestInfo` record then throws away the structure),
     returning `IReadOnlyList<DiscoveredTest>` with
     `{ Id, FullyQualifiedName, DisplayName, ClassName, Namespace, Framework, FilePath, StartLine, EndLine, ProjectPath }`.
     `DiscoverTestsTool` and `CodeLensHandler` both switch to it — which also fixes the current
     inconsistency where the CodeLens matches test attributes by bare name
     (`CodeLensHandler.cs:24-29`) while the MCP tool matches semantically.
   - `RoslynMCP/Services/Testing/TrxParser.cs` — the XML parsing currently inside
     `RunTestsTool.FormatTrxOutput:344-407`, returning
     `IReadOnlyList<TestResult> { FullyQualifiedName, Outcome, Duration, ErrorMessage, StackTrace, StdOut }`.
     `RunTestsTool` keeps its markdown by formatting the parsed results.
   - `RoslynMCP/Services/Testing/TestRunService.cs` — owns the `dotnet test` invocation
     (build/no-build, filter construction, TRX temp path, timeout, cancellation) so both the MCP
     tool and the LSP handler share one implementation.

2. **LSP methods** (`RoslynMCP/Lsp/Handlers/TestHandler.cs`):
   - `roslynSense/testDiscover` `{ projectPath? , uri? }` → `DiscoveredTest[]`. With neither
     argument, enumerate all test projects in the solution.
   - `roslynSense/testRun` `{ runId, projectPath, testIds[]|filter, noBuild }` → final
     `TestResult[]`, plus progress notifications `roslynSense/testRunEvent`
     `{ runId, kind: "started"|"passed"|"failed"|"skipped"|"output"|"finished", testId?, message? }`.
     v1 emits `started` per requested test up front and the real outcomes after the TRX is parsed;
     the event channel exists from day one so a streaming logger can be dropped in later without a
     protocol change.
   - `roslynSense/testDebug` `{ projectPath, filter }` → `{ processId }`. Server-side this reuses
     the exact mechanism `DebuggerService.StartTestSessionAsync:47-164` already implements —
     `dotnet test` with `VSTEST_HOST_DEBUG=1`, scrape `Process Id: N` from stdout — but instead of
     attaching netcoredbg in MI mode it returns the PID so the extension can start a `roslynsense`
     attach session against it. The testhost stays suspended until the debugger attaches and
     resumes, which is what makes breakpoints in the first test reliable.
   - `roslynSense/testCoverage` `{ projectPath, filter? }` → per-file line/branch coverage from the
     existing `CoverageService` Cobertura cache.
   - `roslynSense/testCancel` `{ runId }`.

3. **Extension: `src/testController.ts`.**
   - `vscode.tests.createTestController('roslynSense', 'C# Tests')`.
   - `resolveHandler`: lazy — top level lists test projects, expanding a project discovers its
     tests, grouped namespace → class → method, each item carrying `uri` + `range` so gutter
     decorations and "go to test" work.
   - Run profiles: **Run** (`roslynSense/testRun`), **Debug** (`roslynSense/testDebug` →
     `vscode.debug.startDebugging` with a `roslynsense` attach config), **Coverage**
     (`roslynSense/testCoverage`, fed to `run.addCoverage` with `FileCoverage` /
     `StatementCoverage` so the built-in coverage gutters light up).
   - Result mapping: `passed`/`failed` (with `TestMessage.diff` when the TRX error message parses
     as an xUnit/NUnit expected-vs-actual assertion — cheap regex, big UX win) / `skipped`, plus
     duration.
   - Re-discovery on `didSave` of any `.cs` file inside a test project, debounced 500 ms, and on
     the watched-file events from T1.4.
   - Retire the terminal-based `roslynSense.runTest` command: the CodeLens keeps its
     "▶ Run test" / adds "Debug test" but now routes into the controller so results land in the
     Test Explorer instead of a terminal.

### Files

- `RoslynMCP/Services/Testing/{TestDiscoveryService,TrxParser,TestRunService}.cs` — new.
- `RoslynMCP/Tools/{DiscoverTestsTool,RunTestsTool,FindTestsTool}.cs` — call the services.
- `RoslynMCP/Lsp/Handlers/{TestHandler,CodeLensHandler}.cs`.
- `RoslynMCP/Lsp/Protocol/Testing.cs` — new DTOs.
- `vscode-extension/src/testController.ts` — new; `extension.ts` registration; `package.json` command updates.

### Tests

- `TestDiscoveryServiceTests`: xUnit `[Fact]`/`[Theory]`, NUnit `[Test]`/`[TestCase]`, MSTest
  `[TestMethod]` all discovered with correct FQN and line; a method named `Fact` that is not
  attributed is not discovered (regression against the bare-name matcher).
- `TrxParserTests` against a checked-in TRX fixture including a failure with a stack trace and a
  skipped test.
- `TestRunServiceTests`: runs the sandbox test project, asserts 4 results with correct outcomes.
- Existing MCP `RunTests`/`DiscoverTests` output tests must pass unchanged — that is the guard that
  the extraction was behavior-preserving.

### Definition of done

Test Explorer lists the sandbox's tests grouped by class, running from the tree and from the gutter
both report pass/fail with durations, a failing assertion shows expected-vs-actual, "Debug Test"
stops on a breakpoint inside the test, and the Coverage profile paints covered/uncovered lines.

---

## T1.4 `workspace/didChangeWatchedFiles`

### Problem

Files created, deleted, or renamed outside the editor — `git checkout`, `dotnet new`, scaffolding,
another agent's edits — are invisible until the user manually runs "Reload Workspace". The
extension registers no watcher and the server has no handler. This is the defect most likely to be
blamed on the language server ("it says my new class doesn't exist").

### Design

1. **Client registration.** In `startClient`, set
   `clientOptions.synchronize.fileEvents` to watchers over
   `**/*.cs`, `**/*.{csproj,fsproj,vbproj}`, `**/*.{props,targets}`, `**/*.{sln,slnx}`,
   `**/.editorconfig`, `**/*.globalconfig`, `**/Directory.Packages.props`. Exclude `**/bin/**`,
   `**/obj/**`, `**/node_modules/**` — an unfiltered watcher over `obj/` during a build produces
   thousands of events.
2. **Server handler** — `RoslynMCP/Lsp/Handlers/WatchedFilesHandler.cs`, registered as
   `workspace/didChangeWatchedFiles`. Events are coalesced in a 500 ms window (a `git checkout`
   fires hundreds), then classified:
   - `.cs` **created** → add the document to the containing project's cached snapshot if the
     project already exists; otherwise no-op (the file will be picked up on next project load).
   - `.cs` **deleted** → remove from the snapshot and clear its published diagnostics.
   - `.cs` **changed** while not open in the editor → invalidate the cached text so
     `RefreshDocumentIfStale` picks it up (this already works via mtime, but the event makes it
     immediate rather than on-next-touch).
   - `.csproj` / `.props` / `.targets` / `.sln` / `Directory.Packages.props` → evict the cached
     workspace entry for that solution and reload, reusing the existing
     `ExecuteCommandHandler.reloadWorkspace` path. Guard with a reload lock so a burst of project
     edits produces one reload, and report it via `$/progress` (T2.3).
   - `.editorconfig` / `.globalconfig` → evict analyzer options and the `AnalyzerDiagnosticCache`,
     then refresh diagnostics: severity changes must take effect without a restart.
3. **Refresh fan-out.** After any of the above, call the generalized client refresh (T2.4) so
   diagnostics, code lenses, and inlay hints all re-request.
4. **Rename coalescing.** A rename arrives as delete+create; pair them inside the 500 ms window by
   basename so a rename does not trigger a spurious full reload.

### Files

- `vscode-extension/src/extension.ts` — `clientOptions.synchronize.fileEvents`.
- `RoslynMCP/Lsp/Handlers/WatchedFilesHandler.cs` — new; `LspServer.cs` registration.
- `RoslynMCP/Services/WorkspaceService.cs` — targeted invalidation entry points (add/remove document,
  evict analyzer config), if not already exposed.

### Tests

- `WatchedFilesHandlerTests`: creating a `.cs` file on disk in a fixture project makes it resolvable
  through `LspDocumentResolver` without a manual reload; deleting it makes it unresolvable and
  clears diagnostics; touching `.csproj` triggers exactly one reload for a burst of 50 events;
  touching `.editorconfig` evicts the analyzer cache.

### Definition of done

`git checkout` of a branch that adds files, then immediately navigating to a symbol in a new file,
works with no manual reload — and a burst of file events does not stall the editor.

---

# Tier 2 — Parity

## T2.1 Solution Explorer (Rider-grade)

Target is Rider's Solution Explorer, not VS Code's file tree: the solution's *logical* structure,
including solution folders, a full Dependencies subtree, file nesting, and complete keyboard
operation.

### Missing foundations (build these first)

Three things this needs do not exist anywhere in the repo today:

1. **MSBuild item-metadata evaluation.** Nothing evaluates a project's item model. Even designer
   regeneration matches by filename suffix (`DesignerRegenerationService.cs:52-53`) rather than
   reading `DependentUpon`. `MsBuildLocator` (`Services/MsBuildLocator.cs`) only shells
   `/getProperty:` for single scalar properties. File nesting, `Link`, `Visible`, and
   `PackageReference` enumeration all depend on real evaluation.
2. **Solution folder parsing.** Roslyn's `Solution` model has no concept of solution folders, and
   `ListProjectsTool.ParseSlnAsync:98` hand-parses `Project(` lines while ignoring the
   `NestedProjects` section entirely — everything is flattened. The screenshot's
   `Services → Integrations → Integration.AccountView` hierarchy is exactly what is missing.
3. **A URI scheme for non-file documents.** Source-generated documents are reachable only through
   two MCP text tools, and decompiled sources are materialized as temp files. The extension
   registers no `TextDocumentContentProvider`. Without a scheme, generated-file nodes cannot be
   opened.

New services (in the daemon, shared by both surfaces):

- **`RoslynMCP/Services/ProjectModel/ProjectEvaluationService.cs`** — evaluates a project with
  `Microsoft.Build.Evaluation.Project` (safe in-proc once `Microsoft.Build.Locator` has registered,
  which `WorkspaceService` already does for `MSBuildWorkspace`) and returns:
  - items by type (`Compile`, `None`, `Content`, `EmbeddedResource`, `Page`, `AdditionalFiles`,
    `Folder`) with the metadata that matters: `DependentUpon`, `Link`, `Visible`, `SubType`;
  - `PackageReference` (id, version, `PrivateAssets`, `IsImplicitlyDefined`), `ProjectReference`,
    `Reference`, `Analyzer`;
  - `project.Imports` for the **Imports** node in the screenshot (`Sdk.props`/`Sdk.targets`,
    `Directory.Build.*`, NuGet-injected `.props`/`.targets`), each with its file path so it can be
    opened;
  - `TargetFrameworks` for one framework node per TFM.
  One `ProjectCollection` per solution, unloaded on workspace eviction; results cached against the
  csproj's mtime **and every imported file's** mtime; evaluation is on-demand per expanded project,
  never eagerly for the whole solution, and capped in concurrency.
- **`RoslynMCP/Services/ProjectModel/SolutionFileService.cs`** — `.sln` via
  `Microsoft.Build.Construction.SolutionFile` (gives `ProjectsInOrder`, `SolutionFolder` project
  types, and `ParentProjectGuid` for nesting), `.slnx` via `XDocument` folder elements. Yields the
  folder tree plus "Solution Items" (files attached to solution folders).
- **`RoslynMCP/Services/ProjectModel/FileNestingService.cs`** — see below.
- Package reference to add: `Microsoft.Build` with `ExcludeAssets="runtime" PrivateAssets="all"`,
  matching the existing `Microsoft.Build.Framework` line so it binds against the located SDK rather
  than shipping its own copy.

### Tree shape

```
Solution 'Sandbox' · 7 projects
├─ Solution Items                       (files attached to the solution)
├─ Services · 20 projects                (solution folder)
│  └─ Integrations · 16 projects
│     └─ Integration.AccountView         [startup]
│        ├─ Dependencies
│        │  ├─ Imports                   (MSBuild imports, each openable)
│        │  ├─ .NET 8.0                  (one node per TFM)
│        │  ├─ Packages                  (direct refs; expand → transitive, from project.assets.json)
│        │  ├─ Projects                  (project refs; expand → that project's own Dependencies)
│        │  ├─ Assemblies                (raw Reference / resolved metadata refs)
│        │  └─ Analyzers                 (analyzer assembly → generator → generated files)
│        ├─ Attributes/  Handlers/  Models/  Utils/
│        ├─ IntegrationOptions.cs
│        └─ ServiceExtensions.cs
```

Counts (`· 20 projects`) render as `TreeItem.description`, matching the screenshot. Files use
`resourceUri` so the user's file icon theme applies; logical nodes use `ThemeIcon`s.

### File nesting (`DependentUpon` + rules)

Two independent sources, merged, with explicit metadata winning:

1. **Explicit** — `DependentUpon` metadata from the evaluated item model. This is what makes
   WinForms (`Form1.cs` ← `Form1.Designer.cs`, `Form1.resx`) and WebForms
   (`Page.aspx` ← `Page.aspx.cs`, `Page.aspx.designer.cs`) nest correctly, and it is the one the
   repo's own WebForms/designer support most needs.
2. **Rule-based** — for SDK-style projects that rely on convention instead of metadata. Default rule
   set, overridable via `roslynSense.fileNesting.rules`:
   `*.cs` ← `*.Designer.cs`, `*.g.cs`, `*.generated.cs`; `*.razor` ← `*.razor.cs`, `*.razor.css`;
   `*.cshtml` ← `*.cshtml.cs`; `*.resx` ← `*.*.resx`; `appsettings.json` ← `appsettings.*.json`;
   `*.ts` ← `*.js`, `*.js.map`, `*.d.ts`; `package.json` ← `package-lock.json`, `.npmrc`;
   `Directory.Build.props` ← `Directory.Build.targets`.

Nesting is computed once per project evaluation and cached; a parent that does not exist promotes
its children back to the folder level (no orphan nodes). Toggle:
`roslynSense.solutionExplorer.fileNesting` (default on), matching Rider's "File Nesting" setting.

### Show all files

View-title toggle with three states, mirroring Rider:

- **Project items** (default) — only what the project includes.
- **All files** — everything on disk; files not part of the project render dimmed with a
  "not in project" description. Context actions: *Include in Project* (adds `Compile`/`Content` for
  legacy projects; removes a `<Compile Remove>` for SDK projects) and *Exclude from Project*.
- **All files + ignored** — additionally shows `bin`, `obj`, `.git`, `.vs`, `node_modules`.

The exclusion list is ours, not the editor's `files.exclude`, so the tree does not change shape
because someone hid a folder in their settings.

### Search

Two distinct affordances, because they solve different problems:

- **Filter** — `Ctrl+F` while the view is focused opens an input; the provider rebuilds the tree to
  matching nodes plus their ancestors, auto-expanded, with matched ranges highlighted via
  `TreeItemLabel.highlights`. `TreeView.message` shows "12 matches for 'Account'". `Escape` clears.
  Filtering runs server-side over the cached tree so it stays instant on large solutions.
- **Go to node** — a fuzzy QuickPick over everything in the solution (projects, folders, files,
  packages, generators), ranked server-side via `roslynSense/solutionTreeSearch`, revealing the
  chosen node in the tree.

### Full keyboard support

All bound with `when: focusedView == roslynSense.solutionExplorer`, so nothing leaks globally.
Arrow navigation and type-ahead come free from the tree widget; the rest:

| Key | Action |
| --- | --- |
| `Enter` | Open (files); expand/collapse (containers) |
| `Alt+Enter` | Properties — opens the `.csproj`, or package details for a package node |
| `F2` | Rename (input box; the tree API has no inline editing) — routes through T2.7 fixups |
| `Delete` / `Shift+Delete` | Delete to trash / permanently, with confirmation |
| `Ctrl+C` `Ctrl+X` `Ctrl+V` | Copy / cut / paste files across folders and projects |
| `Ctrl+D` | Duplicate file |
| `Alt+Insert` | New item menu (class, interface, record, enum, folder, file) — Rider's binding |
| `Ctrl+F` / `Escape` | Filter / clear filter |
| `Ctrl+Shift+F` | Find in Files scoped to the selected folder or project |
| `Ctrl+Shift+B` | Build selected project |
| `F5` | Set as startup and debug |

`canSelectMany: true` so every destructive or move operation accepts a multi-selection; commands
receive `(node, nodes[])`. A `TreeDragAndDropController` handles moving files between folders and
projects, with the same namespace fixups as rename.

### Referenced projects and source-generated files

- **Projects** node expands into the referenced project's own `Dependencies` subtree, recursively,
  with a cycle guard and a depth cap — this is the Rider behavior of drilling into a dependency
  without leaving the tree. Selecting a referenced project reveals its real node in the solution.
- **Analyzers** node lists analyzer assemblies → generator types → generated documents, sourced
  from `project.GetSourceGeneratedDocumentsAsync()` (already used by `SourceGeneratedFilesTool`).
  Opening one requires the new **`roslynsense-generated:`** scheme: a `TextDocumentContentProvider`
  in the extension backed by `roslynSense/sourceGeneratedContent`. **The LSP must accept that scheme
  for hover, definition, and references** — mapping it back to the `SourceGeneratedDocument` —
  otherwise the file opens as an inert buffer with no language features, which is worse than not
  showing it. Same requirement applies to the `roslynsense-metadata:` scheme in T2.12; build one
  resolver that handles both.
- **Assemblies** node navigates into decompiled sources through T2.12, so browsing a dependency's
  API never leaves the editor.

### Protocol

- `roslynSense/solutionTree {}` → root nodes.
- `roslynSense/solutionTreeChildren { nodeId, showAllFiles, showIgnored, filter? }`.
- `roslynSense/solutionTreeSearch { query, limit }` → ranked flat matches with their node paths.
- `roslynSense/solutionTreeReveal { uri }` → the node path to reveal for a given file.
- Node: `{ id, parentId, kind, label, description?, resourceUri?, icon?, contextValue, hasChildren,
  sortKey, highlights?, dimmed? }` with `kind` ∈ solution, solutionFolder, project, dependencies,
  imports, import, framework, packages, package, projects, projectRef, assemblies, assembly,
  analyzers, analyzer, generator, generatedFile, folder, file, externalFile.
- Mutations reuse the T2.2/TM.2 command set so tree, editor, and AI all see one implementation.

### Performance rules

Node ids are stable strings so `reveal` and refresh work without rebuilding the world. Refresh is
per-node (`onDidChangeTreeData(node)`), never global. Expansion never triggers synchronous MSBuild
evaluation of unrelated projects. Watcher events from T1.4 invalidate only the affected subtree.
A 500-project solution must expand its root in well under a second, which means the root listing
comes from the `.sln` parse alone — no project evaluation at all until something is expanded.

### Tests

`SolutionFileServiceTests` (nested solution folders in `.sln` and `.slnx`, solution items),
`ProjectEvaluationServiceTests` (DependentUpon read back; imports enumerated; cache invalidated when
an imported `Directory.Build.props` changes), `FileNestingServiceTests` (explicit metadata beats
rules; orphan child promotes; WinForms and Razor sets nest), `SolutionTreeHandlerTests` (lazy
children, filter matches include ancestors, reveal returns a resolvable path, cycle guard on mutual
project references).

### Definition of done

The sandbox opens to a tree matching Rider's shape including solution folders and Dependencies →
Imports/framework/Packages/Projects/Analyzers; `Form1.cs` nests its designer and resx; toggling
"Show All Files" reveals excluded files dimmed; `Ctrl+F` filters live; every operation in the table
above is reachable without the mouse; and a generated file opens with working hover and go-to-definition.

## T2.2 NuGet management (WebView, Rider-style)

A QuickPick cannot express versions, README, dependency groups, vulnerabilities, and multi-project
scope at once. This is a panel.

### Server: a real NuGet client

New packages: `NuGet.Protocol`, `NuGet.Configuration`, `NuGet.Versioning`, `NuGet.Frameworks`
(none referenced today). New service `RoslynMCP/Services/Packages/NuGetService.cs`:

- **Sources** from the real `NuGet.config` chain via `Settings.LoadDefaultSettings(solutionDir)` —
  honoring disabled sources, per-source credentials, and credential providers. This is why the
  network calls belong in the daemon and not in the webview: private feeds need authentication, and
  the AI surface gets the same feed configuration for free.
- `SearchAsync(query, source, includePrerelease, skip, take)` → id, title, authors, summary,
  total downloads, icon URL, latest version, prefix-reserved flag, deprecation, known
  vulnerabilities.
- `GetVersionsAsync`, `GetMetadataAsync(id, version)` → description, README, license, repository
  URL, published date, dependency groups **per target framework**.
- `GetInstalledAsync(scope)` → direct references from `ProjectEvaluationService` (T2.1) plus the
  resolved transitive graph read from `project.assets.json` — no restore required, and each
  transitive entry records which direct package pulled it in.
- `GetUpdatesAsync(scope, includePrerelease)`, and **`GetConsolidationsAsync(solution)`** — package
  ids referenced at differing versions across projects, Rider's Consolidate tab, which is the whole
  reason this feature matters in a multi-project repo.
- Mutations (`install`/`update`/`uninstall`/`consolidate`) shell `dotnet add|remove package` so
  NuGet.config auth and CPM rules apply exactly as they do on the command line. With
  `Directory.Packages.props` present, the version goes to `PackageVersion` there and the csproj gets
  a version-less `PackageReference`. Each mutation reports `$/progress`, then invalidates the
  affected project and fires the T2.4 refresh so squiggles, the tree, and the AI all update at once.
- `GetIconAsync(url)` → cached data URI. The webview's CSP forbids remote images, so icons are
  proxied through the server with a size cap and a disk cache, falling back to a generic glyph.

LSP surface: `roslynSense/nuget/{search,versions,metadata,installed,updates,consolidations,sources,install,update,uninstall,consolidate,icon}`.

### Extension: `src/nugetPanel.ts`

A `WebviewPanel` (`retainContextWhenHidden`) with a strict CSP —
`default-src 'none'; img-src data:; style-src 'unsafe-inline' ${cspSource}; script-src 'nonce-…'` —
and **no remote content of any kind**: every byte comes from the extension or the daemon. Single
hand-written HTML/CSS/JS payload, no bundler, themed entirely with `--vscode-*` CSS variables so it
tracks light, dark, and high-contrast themes.

Layout, following Rider's package tool window:

- **Header** — search box, source dropdown (from `NuGet.config`), prerelease toggle, and a scope
  selector: whole solution or a multi-select of projects.
- **Tabs** — Browse | Installed | Updates | Consolidate. Updates and Consolidate carry counts.
- **Left** — virtualized result list: icon, id, authors, download count, latest version, plus
  deprecation and vulnerability badges.
- **Right** — details for the selection: version dropdown with Install / Update / Uninstall acting
  on the chosen project scope, description, README rendered from **sanitized** markdown, dependency
  groups grouped by target framework, license and repository links (opened via
  `vscode.env.openExternal`, never navigated in the webview), published date, and a prominent banner
  for deprecated or vulnerable packages.
- **Keyboard** — `/` focuses search, `↑`/`↓` move through results, `Enter` installs the selected
  version, `Tab` cycles panes, `Escape` closes; proper ARIA roles and focus rings throughout, since
  a webview gets none of the tree widget's built-in accessibility.
- **State** — a `WebviewPanelSerializer` plus `setState` so a window reload restores the tab, query,
  scope, and selection.

Entry points: the command palette, and the Solution Explorer's Dependencies/Packages context menu
scoped to the clicked project.

Stretch (explicitly not promised): a quick fix on an unresolved type offering to install the package
that provides it. That needs a type→package index, which we do not have and should not fabricate.

### Tests

`NuGetServiceTests` against a local file-system feed fixture (no network in CI): search, versions,
metadata, install writes a `PackageReference`, install under a `Directory.Packages.props` fixture
writes `PackageVersion` and a version-less reference, uninstall removes both, updates detection,
consolidation detection across a two-project fixture at differing versions. Webview logic (filtering,
state reduction) is unit-testable as plain functions; the panel wiring is verified manually.

### Definition of done

"Manage NuGet Packages" opens a themed panel; searching finds packages from the configured feeds
including a private one; installing into two selected projects writes correctly under both classic
and CPM layouts, shows progress, and leaves the workspace immediately consistent; the Updates tab
offers one-click upgrades; the Consolidate tab aligns a package that two projects reference at
different versions.

## T2.3 Progress reporting (`$/progress`)

- **Server:** `RoslynMCP/Lsp/LspProgress.cs` — `Begin/Report/End` helpers that issue
  `window/workDoneProgress/create` then `$/progress` notifications over every registered session
  (`LspSessionRegistry` already has the notify/invoke patterns). Also honor a client-supplied
  `workDoneToken` on requests that carry one.
- **Instrument:** solution/project load in `WorkspaceService.GetOrOpenProjectAsync` ("Loading
  Sandbox.sln — 3/7 projects"), `dotnet restore`, workspace reload, netcoredbg provisioning,
  analyzer first-load, test discovery, workspace diagnostics.
- **Client:** `vscode-languageclient` renders work-done progress natively — no extension code
  beyond ensuring the capability is advertised. Keep the existing `LanguageStatusItem` for steady
  state.

## T2.4 Refresh fan-out for lenses and hints

`LspServer.ScheduleDiagnosticsRefresh` (`LspServer.cs:29-48`) already debounces
`workspace/diagnostic/refresh` at 2 s. Generalize it to `ScheduleClientRefresh(RefreshKind flags)`
that additionally sends `workspace/codeLens/refresh` and `workspace/inlayHint/refresh`, each gated
on the corresponding client capability parsed in `Initialize`. Trigger it from `didChange`,
`didSave`, watched-file events, workspace reload, and analyzer-cache completion. Without this,
reference counts and hints silently rot after any cross-file edit.

## T2.5 Build tasks and problem matcher

- `contributes.taskDefinitions` type `roslynsense` with `task: build|rebuild|clean|test|watch`,
  `project`, `configuration`.
- `registerTaskProvider` producing one task per project plus solution-wide variants, executing
  `dotnet build /clp:NoSummary` (etc.) with `problemMatcher: ["$msCompile"]` — the built-in matcher
  is already correct for MSBuild output, so no custom matcher is needed.
- A dedicated "RoslynSense Build" output channel, and build errors mirrored into a
  `DiagnosticCollection` so they appear in Problems even for files that are not open.
- These tasks are usable as `preLaunchTask`, but T1.2's F5 path does not require them.

## T2.6 Workspace-wide diagnostics

Implement `workspace/diagnostic` with partial-result streaming, advertised via
`DiagnosticOptions(WorkspaceDiagnostics: true)`.

- Scope setting `roslynSense.workspaceDiagnostics`: `off` | `openProjects` (default) | `solution`.
  `openProjects` limits the sweep to projects that own at least one open document plus their
  dependents — the sweet spot between "Problems is empty" and "the machine melts on a 200-project
  solution".
- Reuse per-document `resultId`s so unchanged documents return `unchanged` reports; cap concurrency
  at `Environment.ProcessorCount / 2`; run under `$/progress`; cancel and restart on solution
  changes.
- Analyzer diagnostics are included only from the T1.1 cache, never computed inline during a sweep,
  unless scope is `solution` (in which case the sweep populates the cache deliberately, throttled).

## T2.7 File rename → symbol and namespace fixups

- Advertise `workspace.fileOperations.willRename` / `didRename` with a `**/*.cs` filter.
- `workspace/willRenameFiles` returns a `WorkspaceEdit` that: renames the type when the old file
  name matched a contained type name (via `Renamer.RenameSymbolAsync`, already used by
  `RenameSymbolTool`), and adjusts the namespace when the file moved between folders (Roslyn's
  sync-namespace logic in Features).
- Directory renames are handled by expanding to the contained `.cs` files.
- Guard: if the edit would touch more than N files (default 200), ask for confirmation via
  `window/showMessageRequest` rather than silently rewriting half the repo.

## T2.8 Completion snippets

- Add `InsertTextFormat` (and `TextEditText`) to `RoslynMCP/Lsp/Protocol/Completion.cs`; parse
  `textDocument.completion.completionItem.snippetSupport` in `Initialize`.
- Convert Roslyn's `CompletionChange.NewPosition` into a `$0` tab stop, and emit real placeholders
  for override/interface-implementation completions (`${1:throw new NotImplementedException();}`)
  and for argument lists. Fall back to plain text when the client lacks snippet support.

## T2.9 Semantic token modifiers

- Populate the legend with `static`, `readonly`, `abstract`, `deprecated`, `declaration`,
  `definition`, `documentation`, `defaultLibrary`, `async`, `event`.
- Roslyn's classifier already emits **overlapping** `static symbol` spans alongside the primary
  classification, so the static modifier is a span-overlap map with no extra semantic work. Derive
  `declaration`/`definition` from the syntax node, and `deprecated` from an `[Obsolete]` check done
  only for identifier tokens that resolve to a symbol (cached per compilation).

## T2.10 User-visible server messages

- `RoslynMCP/Lsp/LspLog.cs` — `Info/Warn/Error` fanning out `window/logMessage` (always) and
  `window/showMessage` (warn/error, rate-limited per message key).
- Replace `Console.Error` at the failure sites that currently vanish: project load failure,
  analyzer load/crash, netcoredbg provisioning failure, MSBuild resolution failure, daemon
  handshake failure. The extension routes `logMessage` into its existing output channel.

## T2.11 Multi-root and multiple solutions

- Track solution binding per workspace folder rather than one global
  `roslynSense.solutionPath`; run one LSP client per bound solution.
- Status-bar item shows the active solution for the focused editor and switches binding on click.

## T2.12 Metadata as source

The server can already decompile for go-to-definition, but there is no editor-side scheme for it.

- Register a `TextDocumentContentProvider` for `roslynsense-metadata:` URIs; the server exposes
  `roslynSense/metadataSource { assembly, symbolId }` returning decompiled text plus a mapping so
  navigation *within* decompiled sources keeps working. Documents open read-only with a
  "decompiled from `X.dll`" banner.
- Build **one** virtual-document resolver serving both this scheme and T2.1's
  `roslynsense-generated:`, and teach `LspDocumentResolver` to accept both so hover, definition, and
  references work inside virtual documents rather than opening an inert buffer.

---

# Tier 3 — Debugger depth

Once T1.2 lands, most of the user-facing debugger depth arrives from netcoredbg itself: watch,
locals trees, `setVariable`, `setExpression`, conditional and function breakpoints, exception
filters (`all`, `user-unhandled`), `exceptionInfo`, terminate, and cancel are all advertised in its
`initialize` response. Tier 3 is therefore mostly about (a) closing netcoredbg's real gaps and
(b) raising the **AI mirror** adapter to the same fidelity so an LLM-driven session looks and
behaves like a native one.

## T3.1 Structured debug data in the backend

Today `IDebugBackend` returns formatted strings (`GetLocalsAsync`, `GetStackTraceAsync` →
`string`), which is why `AiDebugAdapter` parses text with regexes (`/^\s+#(\d+)\s+(.*?)(?:\s+at\s+(\S+):(\d+))?\s*$/`)
and why deeper frames lose their file paths.

- Introduce structured returns: `StackFrameInfo { Id, Name, FilePath, Line, Column, IsExternal }`,
  `VariableInfo { Name, Value, Type, VariablesReference, NamedChildCount, IndexedChildCount, Evaluable }`,
  `ThreadInfo { Id, Name, State }`.
- `DebuggerService` gets them from MI (`-stack-list-frames`, `-var-create`/`-var-list-children`);
  `IcorDebugBackend` from its existing object model. Add `GetVariableChildrenAsync(reference)` so the
  Variables view can expand objects and collections lazily.
- The markdown formatting moves up into the MCP tool layer (`DebugInspectTool`), so the AI output is
  unchanged while the DAP surface gets real data.
- `DebugStateStore` / `DebugCommandPipeServer` carry the structured payloads (JSON already), which
  also improves what the prompt hook can tell the LLM.

## T3.2 AI mirror adapter fidelity

With T3.1 in place, `AiDebugAdapter` can honestly report and implement:

- Multi-frame stacks with real file paths and columns; expandable variable trees;
  `supportsSetVariable` backed by a new `SetVariableAsync` (MI `-var-assign`, ICorDebug setter).
- `pause` — currently a hard refusal. Implement `InterruptAsync` (MI `-exec-interrupt`) so the user
  can break into a running AI-owned session instead of being told "the AI debugger cannot pause".
- `terminate` / `supportTerminateDebuggee`, mapped to the existing ownership rules (the LLM still
  cannot stop the user's session; the user *can* stop the AI's, with confirmation).
- `setExceptionBreakpoints` becomes real (filters `all` / `user-unhandled` forwarded to the backend)
  instead of an accepted no-op.
- `exceptionInfo` so the exception popup shows type, message, and inner exceptions.
- Conditional breakpoints already forward a `condition`; add `hitCondition` and `logMessage`
  (logpoint) support at the backend level — see T3.3.

## T3.3 Hit counts and logpoints

netcoredbg reports neither `supportsHitConditionalBreakpoints` nor `supportsLogPoints`, so both must
be emulated on our side for the AI backend, and are simply unavailable for the native session until
netcoredbg gains them:

- **Hit count:** track hits per breakpoint in `PublishingDebugBackend`; on a stop whose breakpoint has
  an unmet hit condition (`>= n`, `= n`, `% n`), auto-continue without surfacing the stop.
- **Logpoints:** on hit, evaluate the interpolated message, emit it as DAP `output`, auto-continue.
- Both are implemented once in the backend decorator and therefore work for MCP tools and the mirror
  adapter alike. Document clearly that the native `roslynsense` session does not support them yet.

## T3.4 Data breakpoints — implemented by emulation

The original entry called this out of scope because no engine exposes a watchpoint: netcoredbg has
no `setDataBreakpoints` and nothing in MI expresses one, and ICorDebug's value breakpoints are not
honored by any current runtime. That is still true, and it is beside the point — the capability
does not have to come from the engine.

`DataBreakpointWatcher` builds it out of the two operations both engines *do* have: step, and
evaluate. Continue becomes a step-and-compare walk; the walk stops on the statement after a watched
expression reads back differently. This lands the same way on both runtimes because it sits in
`PublishingDebugBackend`, above the engine choice, so it covers CoreCLR (netcoredbg over MI) and
.NET Framework (ICorDebug) with one implementation, and reaches the MCP tools, the AI mirror
adapter, and the Framework DAP server at once.

Three consequences are reported rather than hidden:

- **Only `write`.** A read leaves the value alone, so comparing values cannot see one.
  `dataBreakpointInfo` advertises `["write"]` and a `read` request comes back unverified with the
  reason.
- **The stop is one statement late.** A change can only be observed once it has happened.
- **It is slow, and only while armed.** A debugger round trip per statement, bounded by
  `StepBudget`; with no watch set, continue is an ordinary continue.

An expression that stops resolving — the walk stepped into a callee — is skipped rather than read
as a change, so a write inside a callee surfaces on return instead of stopping on every call.

Surface: `dataBreakpointInfo` / `setDataBreakpoints` in `DapServer` and in `AiDebugAdapter`, plus
`DebugWatchValue` / `DebugUnwatchValues` for the AI.

## T3.5 .NET Framework native debugging

`RoslynMCP.Debugger` (ICorDebug) already exists and drives .NET Framework sessions for the AI.
Expose it as a standalone DAP server (a small `--dap` mode on the debug worker) so the `roslynsense`
debugger type can select it when `isNetFramework` is true, giving the user real F5 on Framework
projects too. This is the largest single item in Tier 3 and should be scheduled last; T1.2 ships
with a clear "Framework projects: use the AI session" message until then.

## T3.6 Hot Reload — implemented as real Edit-and-Continue

The earlier recommendation was to ship `dotnet watch` instead of EnC, on the grounds that netcoredbg
has no EnC support. The premise was sound and the conclusion did not follow: **the debugger is not
involved on CoreCLR at all.** `MetadataUpdater.ApplyUpdate` changes a loaded assembly from inside
the process, which is what `dotnet watch` itself uses, so hot reload works on an app that is merely
running — which is the case that matters for the inner loop. Nothing about it needs netcoredbg.

Three pieces:

1. **Delta computation** — `HotReloadService` over `UnitTestingHotReloadService`, the supported
   entry point into Roslyn's EnC engine (the same one behind Visual Studio's Apply Code Changes).
   `StartSessionAsync` captures the built output as the baseline; `EmitSolutionUpdateAsync` returns
   per-module metadata/IL/PDB deltas plus the diagnostics for anything it refuses. Rude edits are
   therefore decided by the compiler that would have to emit them, not guessed at. Updates are
   committed as emitted, so the next apply diffs against the last one rather than the original
   build. Capabilities come from the running runtime (intersection across connected apps) so an
   unapplicable edit is reported at the keyboard rather than at apply time.

2. **CoreCLR apply** — `RoslynMCP.HotReloadAgent`, a dependency-free `net6.0` startup hook injected
   through `DOTNET_STARTUP_HOOKS`, connecting back over a named pipe and calling `ApplyUpdate`.
   Deltas are addressed by MVID, not assembly name, so a name loaded into two contexts cannot be
   corrupted by applying to the wrong one. `DOTNET_MODIFIABLE_ASSEMBLIES=debug` and the hook list
   are both start-time only, which is why hot reload is a launch option (`RunProject hotReload=true`,
   or automatically for a `roslynsense` F5 session) rather than something switchable later.

3. **.NET Framework apply — `ICorDebugModule2::ApplyChanges`, from a break state only.** The
   desktop runtime has no in-process updater, so the app must be under the debugger. The delta
   travels over the debug-bridge pipe (`apply_delta`) when the session lives in another process
   (the editor's `--dap`, or an AI client).

   Getting there cost two real bugs, both worth recording because neither announced itself:

   - **Applying while the target was running faulted the process.** The original code
     async-interrupted a running debuggee (`ICorDebugProcess::Stop`), applied, and resumed.
     `ApplyChanges` access-violated — no HRESULT, no managed exception, the host simply died.

     Pause-apply-resume was then tried properly, on the theory that the missing ingredient was a
     managed stop context rather than the stop itself: break in, walk the app domains for a thread
     with a live `ActiveFrame`, adopt it, apply, resume. **It faulted identically.** So the desktop
     CLR does not want a synchronized process with a thread selected; it wants a stop that arrived
     through a debug event. That is why Visual Studio and Rider require you to already be in break
     mode rather than breaking on your behalf — Rider's `ApplyEncChangesSync` asserts the debugger
     session's main thread and runs off a paused session.

     The engine therefore never applies against a running target. An apply that arrives without
     a genuine debug-event stop — or at a stop whose thread is not in user code of the edited
     module's own app domain — is queued, reported as queued, and flushed at the next
     breakpoint, step or exception stop. The alternative to that queue is a dead process.
   - **`TrySetJITCompilerFlags(CORDEBUG_JIT_ENABLE_ENC)` had its result thrown away.** A module
     that fails to flag is not updatable, and `ApplyChanges` faults on it rather than failing, so
     the one signal that predicts a crash was being discarded. The HRESULT is now checked and an
     unflagged module is never offered as a hot reload target.

   `InProcessDebugEngine.ApplyDeltaAsync` refuses regardless: in the tool, "the host" is the
   editor's language server and every other chat's loaded workspace, which is not something to
   stake on a native call that faults instead of failing. Only `WorkerDebugEngine` — a separate,
   disposable process — may make it. That guard is what turned the second reproduction of the
   crash from an outage into a reported error. `DebugEngineFactory` therefore routes every
   .NET Framework session through a worker whenever one is staged, even when the bitness
   matches; in-process is only the fallback for installs without workers.

   Three further things the runtime does *not* do for you, all now handled — `ApplyChanges` takes
   metadata and IL and updates the runtime, and nothing else:

   - **The PDB delta goes to the debugger's own symbol reader.** It was being discarded, which
     left every line number, sequence point and local scope in an edited method pointing at
     pre-edit source. `SymUnmanagedReader.UpdateSymbolStore` takes the delta directly, which is
     what MDbg's `ApplyEdit` does. A portable reader has no equivalent, so its cache is dropped
     and the staleness is reported rather than hidden.
   - **Breakpoint bindings are invalidated for the edited module.** After an edit a method token
     no longer identifies one piece of code — the edited method has a new version, and a binding
     made against the old one resolves to it. Dropping the bindings returns those breakpoints to
     pending; the specs survive, so they rebind.
   - **A failed apply poisons the session.** There is no rollback, so the runtime's metadata and
     the debugger's view can disagree from that point on. Further edits would build on that, so
     they are refused.

   `FunctionRemapOpportunity` / `ICorDebugILFrame2::RemapFunction` is handled: a frame stopped
   inside an edited method jumps to the edited version at the mapped sequence point instead of
   finishing on the old code — the Rider/Visual Studio behaviour. If the remap fails the frame
   falls back to finishing the old version, which is the documented default. Applies are also
   MVID-keyed across app domains: every loaded instance of the edited module with the same MVID
   receives the delta, provided its domain has a safely-stopped user-code thread.

   `RoslynMCP.Tests/FrameworkHotReloadTests.cs` proves the whole path: an MSBuild-built `net48`
   x86 target, launched under the worker, broken at a breakpoint, edited, applied, resumed, and
   observed returning the new value. Gated behind `ROSLYNSENSE_TEST_FX_HOTRELOAD=1` — it drives a
   native call that can still fault the worker, and a crash aborts a run rather than failing a case.

Surface: `roslynSense/hotReload{Start,Apply,Stop,Status,Environment}`, the `ApplyHotReload` /
`StopHotReload` MCP tools, and `Apply Hot Reload` plus `roslynSense.hotReload.applyOnSave` in the
extension. Rude edits are published as diagnostics with file and line, with a Restart action.

---

# Tier M — MCP surface

The tiers above are editor-facing, but several of them build server-side capability the AI cannot
reach today. Two principles keep the surfaces honest:

- **Anything the editor can *do*, the AI should be able to do too** — otherwise the AI resorts to
  shelling `dotnet` through Bash, which mutates project files behind the loaded workspace's back and
  leaves it stale.
- **Anything the user can *see*, the AI should be able to ask about** — the debug bridge already
  proved this pattern; editor context is the same idea, one step earlier in the loop.

## TM.1 Analyzer diagnostics on the MCP path (rides T1.1)

Two defects fixed by T1.1 are MCP-side, not LSP-side, and should be called out because they change
existing tool output:

- `AnalyzerService.cs:82` passes no `project.AnalyzerOptions`, so `get_roslyn_diagnostics` ignores
  `.editorconfig` / `.globalconfig` severities today. After the fix, a rule downgraded to
  `suggestion` reports as a suggestion and one set to `none` disappears. Existing MCP output
  snapshots will shift.
- `GetRoslynDiagnosticsTool.GetProjectDiagnosticsAsync:117-143` never calls `AnalyzerService` at
  all — project-wide requests are compiler-only while single-file requests run analyzers. Fix by
  routing both through the same merge, using the per-tree analyzer API so project mode stays
  affordable.

New tool: **`get_solution_diagnostics`** `{ severityFilter, scope: "openProjects"|"solution", maxResults }`
sharing T2.6's sweep and the T1.1 cache. "What is broken across this solution" currently requires
the AI to loop `get_roslyn_diagnostics` per file, which is slow and usually incomplete.

## TM.2 Project and package mutation

The largest genuine hole. There is no MCP tool to add a package, add a project reference, add a
file to a project, or create a project — so the AI shells out to `dotnet add package …`, and the
daemon's loaded workspace does not learn about it until something else forces a reload.

T2.1/T2.2 build exactly these operations server-side for the Solution Explorer. Expose the same
services as tools (`[InProcessOnly]` is not required — these are workspace mutations that belong in
the daemon so the reload is immediate and shared):

- `list_packages` / `search_packages` / `add_package` / `remove_package` / `update_package` — all
  over T2.2's `NuGetService`, so the AI gets the same configured feeds, private-feed auth, and
  Central Package Management handling as the panel, instead of shelling `dotnet add package`.
- `add_project_reference` / `remove_project_reference`.
- `add_file` `{ projectPath, relativePath, kind: class|interface|record|enum|empty }` with namespace
  inferred from folder, and `delete_file`.
- `create_project` `{ template, name, targetFramework, addToSolution }` and `add_project_to_solution`.

Every one of these ends by invalidating the affected workspace entry and firing the T2.4 refresh, so
the AI's next `find_usages` sees the new state and the user's editor updates at the same moment.

## TM.3 Editor context

The debug bridge taught the AI what the user is debugging. It still has no idea what the user is
*looking at*. Mirror the `EditorDebugStateStore` design with an `EditorContextStore`
(`%TEMP%\roslyn-sense\editor-context\{solutionHash}.json`), written by the extension on a debounced
selection/visibility change and read by a new tool:

- **`get_editor_context`** → active file, cursor position and enclosing symbol, selection text, open
  tabs, dirty files, and the diagnostics currently visible in the active editor.

Extension side is a small module publishing over a new `roslynSense/editorContext` notification.
Same one-shot prompt-hook injection as the debug bridge (`hooks/drain-notifications.mjs`), so a
question like "why does this fail?" with no other context resolves to the file and method the user
is actually staring at.

Privacy note: this ships opt-out via `roslynSense.shareEditorContext` (default on) and the store is
per-solution in the user's own temp dir, same as the existing debug stores.

## TM.4 Structured tests and formatting

- T1.3's `TestRunService`/`TrxParser` extraction makes structured results available; add
  **`get_test_failures`** `{ runId? }` returning failures with file/line resolved from the stack
  trace so the AI can jump straight to the assertion instead of re-parsing markdown it just printed.
  `run_tests` keeps its current markdown output unchanged.
- **`format_document`** `{ filePath, range? }` — the LSP has formatting; the AI has none, so
  generated code goes in unformatted and the next `.editorconfig`-driven diff is noisy.
- **`rename_file`** `{ oldPath, newPath }` riding T2.7's namespace/type fixups — today the AI can
  rename a symbol but not a file without losing the type-name/namespace correspondence.

## TM.5 Debugger tools for the structured backend (rides T3.1)

T3.1 replaces the string-returning inspection APIs with structured data. That unlocks tools the AI
cannot express today:

- **`debug_pause`** — currently impossible; the AI can only wait for a breakpoint.
- **`debug_set_variable`** `{ name|expression, value }`.
- **`debug_select_frame`** `{ frameId }` so evaluation happens in a caller's scope rather than only
  the top frame.
- **`debug_expand`** `{ variablesReference }` for object and collection children, replacing the
  current flat `name = value` dump.
- `debug_status` / `debug_inspect` keep their markdown, now rendered from the structured payloads.

Ownership rules are unchanged: the AI may pause and inspect its own session; the user's session
stays read-plus-control-only as it is today, and the AI still cannot stop it.

## TM.6 Symmetry checklist

New capability lands with a deliberate answer for both surfaces:

| Capability | LSP / editor | MCP / AI |
| --- | --- | --- |
| Analyzer diagnostics | T1.1 squiggles | TM.1 tool fix + solution sweep |
| Launch / debug | T1.2 F5 | already covered by `debug_*` + `run_project` |
| Tests | T1.3 Test Explorer | `run_tests` + TM.4 structured failures |
| Packages / project files | T2.1, T2.2 UI | TM.2 tools |
| File rename fixups | T2.7 | TM.4 `rename_file` |
| Formatting | existing LSP | TM.4 `format_document` |
| What the user is doing | n/a | TM.3 editor context |
| Debugger depth | T3.2 native UI | TM.5 tools |

---

# Sequencing

Historical — every step below has shipped. Kept because the order it was built in explains why
several things depend on each other, and because the reasoning still applies to the items in
[What is still missing](#what-is-still-missing).

Ordered by value per unit of risk. Each numbered step is independently shippable and testable.

1. **T1.1 analyzer diagnostics** (+ **TM.1**, same diff) — smallest change, largest visible payoff,
   no new protocol surface.
2. **T1.4 watched files** — small, and it removes the "stale workspace" class of bug reports that
   would otherwise be blamed on everything built after it.
3. **T2.4 refresh fan-out** + **T2.3 progress** — cheap infrastructure that T1.2/T1.3 both consume.
4. **T1.2 native debugging** — highest user-visible payoff; unblocks most of Tier 3 for free.
5. **T1.3 Test Explorer** — depends on the service extractions and (for Debug Test) on T1.2.
6. **T2.10 messages**, **T2.8 snippets**, **T2.9 token modifiers** — small polish batch.
7. **T2.5 tasks**, **T2.6 workspace diagnostics**, **T2.7 file rename**.
8. **TM.3 editor context** — small, independent, and it improves every AI answer immediately.
9. **T2.1 Solution Explorer**, **T2.2 NuGet** + **TM.2 mutation tools** — the largest Tier 2 items;
   build the services once and expose both surfaces in the same pass.
10. **T3.1 structured debug data** → **T3.2 mirror fidelity** + **TM.5 debug tools** → **T3.3 hit
    counts/logpoints**.
11. **T2.11 multi-root**, **T2.12 metadata source**, **T3.5 Framework DAP**.

## Cross-cutting acceptance test

A single scripted pass through the sandbox (`D:\Sources\roslyn-sandbox`) with **every**
ms-dotnettools extension disabled:

1. Open the folder cold; solution load shows progress and completes.
2. A StyleCop violation squiggles; `.editorconfig` downgrades it; the lightbulb offers a fix and a
   suppression.
3. `git checkout` a branch adding a file; navigate to a symbol in it with no manual reload.
4. Test Explorer lists and runs the tests; one fails with expected-vs-actual; Debug Test stops on a
   breakpoint; Coverage paints gutters.
5. F5 launches the web app, a breakpoint in `OrderCalculator.Total` hits, watch and `setVariable`
   work, an exception filter breaks on throw.
6. Meanwhile an MCP chat starts its own debug session; both sessions coexist, the shared breakpoint
   set stays consistent, and neither can stop the other's session.

# Project / solution load performance

Analysis only — no code was changed. Every claim below is either cited to `file:line` in this
repo, or explicitly marked as an estimate / speculation.

Motivating evidence: a real 87-project solution loaded ~13
projects up front, then added the remaining ~74 **one at a time** over many minutes, each logged as
`[WorkspaceService] Incrementally loaded 'X.csproj' … (+N project(s); M loaded)`; several of those
each spawned their own `dotnet restore`; and projects whose MSBuild load reported a failure went on
to produce a wall of CS0012/CS1061.

---

## 1. Summary: where the time actually goes

### 1.1 There is no "solution open". There are N project opens.

`MSBuildWorkspace.OpenSolutionAsync` is **never called anywhere in this repository** (grep for
`OpenSolutionAsync` returns nothing outside doc text). The only Roslyn load call is
`RoslynMCP/Services/WorkspaceService.cs:502` :

```csharp
openedProject = await msbuildWorkspace.OpenProjectAsync(
    normalizedPath, cancellationToken: openLinked.Token)
```

and the incremental one at `WorkspaceService.cs:686`. So:

* The initial ~13 projects are **not a filtered solution open**. They are the seed project plus the
  transitive `ProjectReference` closure that Roslyn pulls in for free — exactly as the design
  comment at `WorkspaceService.cs:496-501` says: *"Open ONLY the requested project (Roslyn
  additionally pulls in its transitive ProjectReferences)."*
* The remaining ~74 are **purely demand-driven**, and on the `open_solution` path the demand is a
  plain sequential loop: `RoslynMCP/Tools/SolutionSessionTool.cs:146-163`

  ```csharp
  foreach (var project in projects)          // every .csproj in the .sln, in .sln order
      await WorkspaceService.GetOrOpenProjectAsync(project, ...);
  ```

  Each project that is not already in the seed's closure falls into
  `EnsureProjectLoadedAsync` (`WorkspaceService.cs:664`), which logs the exact line observed
  (`WorkspaceService.cs:704-707`).

**This is the single biggest structural finding.** The warm loop walks the solution in `.sln`
declaration order, which is alphabetical-ish and says nothing about the dependency graph. If it
walked the graph *roots* first (projects nothing else references — apps, test projects), each
`OpenProjectAsync` would pull in its whole transitive closure in one MSBuild pass, and a typical
backend solution would be covered by a handful of opens rather than 87.

The LSP path does **not** eagerly load (verified: `LspServer.Initialize` only calls
`WorkspaceService.BindSolution`, `WorkspaceService.cs:864-868`; preload is opt-in,
`WorkspacePreloadHostedService.cs:79-84`; `DaemonServer.cs:63-64` explicitly declines to warm). So
the observed behaviour came from `open_solution` or from a per-file drip via
`LspDocumentResolver` — both funnel into the same place.

### 1.1a Each of those 87 opens spawns and tears down its own BuildHost

This is the cost that was invisible from the logs, and it may be the largest single one.

Roslyn 5.6.0 (`Microsoft.CodeAnalysis.Workspaces.MSBuild` 5.6.0, pinned at
`RoslynMCP/RoslynMCP.csproj:27-33`; source commit `c0573ed0a7dc3e3b4d2e70da47f97cc51a35524f`)
creates a **fresh `BuildHostProcessManager` inside `MSBuildProjectLoader.LoadInfoAsync` for every
top-level `OpenProjectAsync`/`OpenSolutionAsync` call**, and disposes it (`await using`) when that
call returns. Within one call it keeps at most one BuildHost process *per kind*
(`NetCore` / `NetFramework` / `Mono`) and reuses it for every project that call touches.

So the per-project subprocess bill on a cold 87-project solution is:

| Per project | Source |
|---|---|
| 1 × `dotnet restore` (when `obj/project.assets.json` is missing) | `WorkspaceService.cs:1497-1509` |
| 1 × BuildHost-netcore spawn + teardown (+1 net472 for a legacy project) | one `BuildHostProcessManager` per `OpenProjectAsync` call, i.e. per `WorkspaceService.cs:686` |

≈ **170 subprocess launches, fully serialized**, where a single solution-wide load would need one
restore and one BuildHost.

There is a second, subtler cost hidden in the same place: each BuildHost batch gets its own MSBuild
`ProjectCollection`, so the SDK targets, `Directory.Build.props`, `Directory.Packages.props` and
every other import are **re-parsed from scratch for every one of the 87 calls**. Inside a single
call they are parsed once and shared across all projects in the batch.

### 1.2 Everything expensive happens inside one per-solution semaphore

`EnsureProjectLoadedAsync` takes `entry.LoadGate` at `WorkspaceService.cs:675` and releases it at
`:716`. **Four awaits sit inside it, three of them unbounded:**

| # | Line | What | Bounded? |
|---|------|------|----------|
| 1 | `:685` | `EnsureRestoredAsync` → up to one `dotnet restore` **subprocess** | no |
| 2 | `:686` | `ws.OpenProjectAsync` → BuildHost spawn + MSBuild design-time evaluation + BuildHost teardown (§1.1a) | no (300 s cap, `:30-32`) |
| 3 | `:693` | `ApplyPostOpenPipelineAsync` → whole-directory `File.Copy` per analyzer dir **and** one `GetCompilationAsync` per new project | no |
| 4 | `:695` | `s_cacheLock.WaitAsync` — takes the **process-wide** lock while holding the gate | short |

The gate's own XML doc (`WorkspaceService.cs:1708-1711`) justifies itself as:

> *"Serializes incremental `OpenProjectAsync` mutations of this workspace (MSBuildWorkspace is not
> safe for concurrent opens; reads stay safe via immutable solution snapshots)."*

**That justification is real and still holds at 87 projects** — `OpenProjectAsync` does a
read-modify-write of the workspace's project map, and two concurrent opens can each miss the
other's projects and double-add shared references. But it justifies **only item 2**. Items 1, 3 and
4 are inside the gate for no stated reason, and items 1 and 3 are the bulk of the wall-clock time on
a cold solution.

### 1.3 One `dotnet restore` subprocess per project, serialized

`EnsureRestoredAsync` (`WorkspaceService.cs:1484-1543`) spawns `dotnet restore "<project>"`
(`:1502`) whenever `obj/project.assets.json` is missing (`:1492-1493`). It is called:

* once for the seed, outside the gate (`:482`), and
* once **per incrementally added project, inside the gate** (`:685`).

Worst case on a cold 87-project solution: **87 `dotnet` subprocess launches, fully serialized**.
Estimate (not measured here): SDK host start ~0.7–1.5 s plus NuGet graph walk, so 3–10 s each —
i.e. several minutes of pure subprocess time on the critical path, which matches "many minutes".

Related correctness note: the freshness check is *existence only*. A stale `project.assets.json`
(e.g. after a `Directory.Packages.props` edit) is never refreshed.

### 1.4 Dead work under the process-wide cache lock

`GetOrOpenProjectAsync` holds `s_cacheLock` (`:354`) across `TryFindOwnerSolutionKey` (`:364`),
which does disk I/O:

* `PathHelper.FindNearestSolution` — 2 × `Directory.GetFiles` per ancestor level (`PathHelper.cs:168-181`)
* `PathHelper.GetProjectsFromSolution` — `File.ReadAllLines(.sln)`, uncached (`PathHelper.cs:187-218`)
* a membership scan, up to N × `Path.GetFullPath` (`WorkspaceService.cs:1127-1128`)
* `PathHelper.RequiresMsBuild(sln)` → `PathHelper.IsLegacySolution` (`PathHelper.cs:65-99`) which
  **opens every .csproj in the solution** via an uncached `StreamReader` + `new Regex(...)`
  (`PathHelper.ReadProjectSdk`, `PathHelper.cs:15-54`).

And the result of that last one **is thrown away**. `TryFindOwnerSolutionKey` returns
`(slnKey, isLegacy)` (`:1132`) and *both* callers take only `.slnKey`:

* `WorkspaceService.cs:364` — `TryFindOwnerSolutionKey(normalizedPath).slnKey`
* `WorkspaceService.cs:1107` — `TryFindOwnerSolutionKey(Path.GetFullPath(projectPath)).slnKey`

Order-of-magnitude estimate for an all-SDK 87-project solution with 87 cache misses: ~174 file
opens per miss × 87 ≈ **15,000 file opens, serialized under the process-wide lock**, of which
roughly half compute a value nobody reads. That lock is also what every cache *hit* (hover,
completion, semantic tokens) has to acquire — so an interactive gesture queues behind it.

### 1.5 A full Compilation is forced for every project, at load time, inside the gate

`ApplyPostOpenPipelineAsync` (`WorkspaceService.cs:1382-1405`) ends with
`InjectMissingFrameworkReferencesAsync`, whose first act per project is
(`WorkspaceService.cs:1555-1560`):

```csharp
var compilation = await project.GetCompilationAsync(cancellationToken);
if (compilation is null) continue;
var objectType = compilation.GetSpecialType(SpecialType.System_Object);
if (objectType.TypeKind != TypeKind.Error) continue;
```

`Project.GetCompilationAsync` parses **every document in the project** and binds every metadata
reference. To answer "does `System.Object` resolve?", which depends only on
`project.MetadataReferences`. On an 87-project solution this parses the entire codebase during
load, on the critical path, holding the gate.

A `CSharpCompilation.Create("probe", references: project.MetadataReferences)` answers the identical
question without touching a single syntax tree.

*(High confidence, not verified in this pass: the compilations so created are retained by the
workspace's current-solution snapshot for as long as it lives, because a bare `Workspace` host has
no `IProjectCacheHostService` to release them. If so this is also the dominant memory cost.)*

### 1.6 Shadow copying runs synchronously inside the gate, and copies whole directories

`RebindAnalyzerReferencesToShadowLoader` (`WorkspaceService.cs:1425-1477`) calls
`loader.Register(fileRef.FullPath)` (`:1456`) → `ShadowCopyManager.GetLoadPath` →
`EnsureShadowDirectory` (`ShadowCopyManager.cs:143-181`), which copies **every** `.dll`, `.pdb` and
`.json` in the source directory (`:159-174`) under a lock, synchronously.

`NeedsShadowCopy` (`ShadowCopyManager.cs:65-72`) returns `true` for *anything* not under the NuGet
global packages folder and not already under the shadow root. That includes:

* project-output source generators under `bin/…` (intended), and
* **analyzers from a repo-local `packages\` folder** — i.e. every packages.config-era legacy project
  in a mixed solution. Those directories are copied wholesale.

The per-source-directory sharing itself is correct (`ShadowCopyManager._shadowDirectories`,
`:27`, `:145-146`), so N projects sharing one generator cost one copy. The problem is *where* the
copy happens (in the gate) and *how much* it copies.

### 1.7 A failed incremental add opens a second workspace — and steals the first one's projects

`GetOrOpenProjectAsync` lines `396-425`: if `EnsureProjectLoadedAsync` throws, or the project is
still not resolvable afterwards, it does

```csharp
solutionPath = null;
loadKey = normalizedPath;      // :422
continue;
```

and on the next iteration becomes the loader for a **standalone `MSBuildWorkspace`** keyed by the
`.csproj` (`:456`, `:477`, `:582`). Then `RegisterProjectMappingsLocked` (`:1058-1067`) runs:

```csharp
s_projectToCacheKey[requestedProjectPath] = cacheKey;
foreach (var project in workspace.CurrentSolution.Projects)
    if (!string.IsNullOrEmpty(project.FilePath))
        s_projectToCacheKey[Path.GetFullPath(project.FilePath!)] = cacheKey;   // unconditional
```

That overwrite is unconditional, so **every project in the fallback workspace's transitive closure
is re-pointed away from the solution workspace**. Consequences, all of which match the reported
symptoms:

* Two `MSBuildWorkspace`s run design-time builds over an overlapping project set. Both write
  `obj\X.csproj.AssemblyReference.cache` → **`Could not write state file 'X.csproj.AssemblyReference.cache' … used by another process`**. This is the most likely mechanism for that observed error.
* Double the MSBuild time and double the retained memory for the overlapping projects.
* The solution is silently split: symbols from the two workspaces are not comparable, so
  find-usages/rename narrow without saying so.
* `MaxCachedWorkspaces` is **4** (`WorkspaceService.cs:62-64`). A few of these evict the real
  solution workspace, and the next request reloads it from scratch.

### 1.8 Phantom diagnostics are produced by design, not by accident

Workspace failures are written to stderr and discarded (`WorkspaceService.cs:314-318`):

```csharp
workspace.RegisterWorkspaceFailedHandler(args => {
    var writer = diagnosticWriter ?? Console.Error;
    writer.WriteLine($"Workspace warning: {args.Diagnostic.Message}");
}, null);
```

Nothing correlates a `WorkspaceDiagnostic` back to a `ProjectId`, and no consumer of diagnostics
checks whether the project loaded cleanly — `DiagnosticsHandler.CompilerDiagnosticsAsync`
(`Lsp/Handlers/DiagnosticsHandler.cs:107-114`) and `WorkspaceDiagnosticsHandler.DiagnoseProjectAsync`
(`:223-238`) both report whatever the semantic model says.

Worse, the framework-reference repair actively *enables* the phantom diagnostics: when a project's
references failed to resolve, `InjectMissingFrameworkReferencesAsync` (`WorkspaceService.cs:1549-1584`)
detects it (`System.Object` is an error type) and injects mscorlib/System.Runtime etc. — but it
cannot inject the NuGet and project references that are actually missing. The project then compiles
far enough to produce CS0012 ("type is defined in an assembly that is not referenced") and CS1061
for everything else. **We repair the project just enough to make it complain.**

Note also that the observed MSBuild messages are of two very different kinds and are currently
treated identically:

* harmless noise — `PackageReference X will not be pruned…`
* genuinely load-breaking — `The referenced project ..\Foo.csproj does not exist`,
  `Could not write state file … used by another process`

**There is a precise, structural way to detect the degraded case — better than parsing messages.**
In Roslyn 5.6.0, when a `<ProjectReference>` path does not resolve to a file on disk,
`Worker_ResolveReferences` still records the reference against a synthesised `ProjectId` that has
no backing `ProjectInfo` anywhere in the solution:

```csharp
var unknownProjectId = _projectMap.GetOrCreateProjectId(projectFileReference.Path);
builder.AddProjectReference(CreateProjectReference(from: id, to: unknownProjectId, aliases));
```

so the test is simply:

```csharp
project.ProjectReferences.Any(r => solution.GetProject(r.ProjectId) is null)
```

Likewise, a project whose MSBuild evaluation fails outright is still added to the solution, but as
`ProjectFileInfo.CreateEmpty` — no documents, no references — so `project.Documents` empty *and*
`project.MetadataReferences` empty is the second signal.

By contrast, message correlation is genuinely weak: for exactly these two cases Roslyn raises a
plain `WorkspaceDiagnostic` with **no `ProjectId`** (the `ProjectDiagnostic` subclass exists but is
used for other things), so the only correlation available from `RegisterWorkspaceFailedHandler`
is substring-matching an absolute path out of `diagnostic.Message`. Use the structural checks;
keep the message stream only as the human-readable *reason*.

### 1.9 Incidental gestures can still expand the workspace

Two live paths, both verified by reading the call chain:

**(a) Solution Explorer "Dependencies" node → real Roslyn load.**
`SolutionTreeHandler.DependencyGroupsAsync` is otherwise evaluation-only, but line `:290` calls
`VirtualDocumentHandler.ListGeneratedAsync`, which calls
`WorkspaceService.GetOrOpenProjectAsync` (`Lsp/Handlers/VirtualDocumentHandler.cs:55`) purely to
find out whether the project has source-generated files. Expanding a node in the tree therefore
triggers a design-time build. This contradicts `SolutionTreeHandler`'s own header comment (`:8-16`).

**(b) The C# reference code lens can still start a whole-solution proto consumer load.**
The proto pack's new opt-in budget covers the `.proto`-side gestures, but not this chain:

```
CodeLensHandler.ResolveAsync                     Lsp/Handlers/CodeLensHandler.cs:174
  → NavigationHandlers.AllReferencesAsync        Lsp/Handlers/NavigationHandlers.cs:239-242
      foreach (ILanguageReferenceContributor c)  → c.ReferencesAsync(symbol, project, ct)
  → ProtoLanguage.ReferencesAsync                Languages/Proto/ProtoLanguage.Contributors.cs:204-212
      → UsagesOfAsync(..., ExplicitSearchBudget)   // 15 s, ProtoReferenceService.cs:257
        → SearchScopeAsync(budget: 15 s)           → LoadConsumersAsync over the whole solution
```

`ProtoLanguage` is declared `ILanguageReferenceContributor` at
`Languages/Proto/ProtoLanguage.Contributors.cs:36`. The chain is gated by
`InterestingSymbolKinds` + `HostsProtobufAsync` + "the symbol maps to a `.proto` declaration"
(`DeclaringLinesAsync`, `:244-265`), so it does not fire for every caret — but on a gRPC backend a
code lens over generated-adjacent C# is exactly the case that survives all three gates, and
`codeLens/resolve` is scroll-driven. There is currently no way for the contributor interface to say
"this request is incidental": `ILanguageReferenceContributor.ReferencesAsync`
(`Languages/Abstractions/LspContributors.cs:124-127`) has no budget/urgency parameter.

### 1.10 `FindContainingProjectAsync` loads projects in order to classify a file

`WorkspaceService.cs:725-773`. For each ancestor directory it opens **every** `.csproj` in that
directory through `GetOrOpenProjectAsync` (`:747-748`) and then asks whether the document is in it
(`:750`). This is the hottest path in the product — every LSP request routes through
`LspDocumentResolver.ResolveAsync` (`Lsp/LspDocumentResolver.cs:27`) and most MCP tools through
`ToolHelper.ResolveFileAsync` (`Services/ToolHelper.cs:43,52`). In the common case (one `.csproj`
per directory, file under it) the answer is available from the file system or from
`ProjectEvaluationService` without any MSBuild design-time build.

---

## 2. Answers to the specific questions

**Q1 — why a subset up front, then one at a time?**
There is no solution open at all (§1.1). The up-front set is `OpenProjectAsync(seed)` + Roslyn's
transitive `ProjectReference` closure. The drip-feed is demand-driven, and on the `open_solution`
path the demand is `SolutionSessionTool.WarmWorkspaceAsync`'s sequential `foreach` over the `.sln`
project list (`SolutionSessionTool.cs:146-163`). Nothing is filtered; the order is just wrong.

**Q2 — where is concurrency lost, and does MSBuildWorkspace have knobs?**
Four places. (i) The warm loop is sequential and unordered (§1.1). (ii) The `LoadGate` serializes
restore + open + post-open pipeline for *unrelated* projects in the same solution (§1.2); only the
`OpenProjectAsync` call itself needs it, and that need is real. (iii) Restores are inherently
parallel subprocesses but are run one at a time inside the gate (§1.3). (iv)
`ProtoReferenceService.LoadConsumersAsync` (`:711-732`) is a sequential `foreach` — though see O11d:
parallelising it *before* narrowing the gate would only move the contention.

**MSBuildWorkspace has no parallelism knob**, and this changes the shape of the answer. In Roslyn
5.6.0, `MSBuildProjectLoader.Worker.LoadAsync` is a plain sequential `foreach` over the requested
project paths, and `Worker_ResolveReferences` resolves `<ProjectReference>`s recursively with plain
`await`s — no `Task.WhenAll`, no `Parallel.ForEach`, no semaphore. `MSBuildWorkspace` exposes only
`Properties`, `LoadMetadataForReferencedProjects`, `SkipUnrecognizedProjects` and
`AssociateFileExtensionWithLanguage`. So `OpenSolutionAsync` is *not* a way to get parallelism
either — its value is that it is **one batch** (§1.1a), not that it is concurrent.

Doing the parallelism ourselves is possible but not cleanly: `MSBuildProjectLoader` is public and
`LoadProjectInfoAsync(string, ProjectMap?, …)` can be called concurrently, but (a) each call spins
up its own BuildHost set, (b) `ProjectMap`'s dictionaries are unsynchronised so sharing one to
de-duplicate is a data race, and without sharing it two concurrent loads of a common third project
produce two different `ProjectId`s for the same file, and (c) `Workspace.OnProjectAdded` /
`OnSolutionAdded` are `protected internal`, so applying the results needs either a derived
`Workspace` or more of the `SetCurrentSolution` reflection this file already carries
(`WorkspaceService.cs:84-89`). **Recommendation: do not go down this road.** Reduce the *number* of
loads (O1/O13) instead of trying to overlap them.

**Q3 — should restore be per-project or per-solution?**
Per-solution, once, before the first open. Today it is per-project, lazily, inside the gate, keyed
on file existence only (§1.3). Roslyn will never do it for us — there is no restore logic anywhere
in `Microsoft.CodeAnalysis.Workspaces.MSBuild` 5.6.0, and the feature request for it
(dotnet/roslyn#52293) is still open. Caveat: `dotnet restore <sln>` does not handle
packages.config-era projects, so a mixed solution needs a fallback to the current per-project path.

**Q4 — are we paying for design-time builds we do not need?**
Yes, in three places: the framework probe forces a full compilation of every project to ask a
metadata-only question (§1.5); `FindContainingProjectAsync` loads projects to decide file ownership
(§1.10); and the Solution Explorer's Dependencies node loads a project to ask whether it has
generated files (§1.9a). `ProjectClassifier.Classify(string)` and `ProjectEvaluationService` are the
counter-examples — they already answer from the XML / a private `ProjectCollection` (see §4).

**Q5 — what can be deferred, what must stay eager?**
Deferrable: the shadow copy (only needed before the first `GetCompilationAsync` that touches an
analyzer — but see the invariant below), the framework probe (or replace it), the whole-solution
warm (nothing requires 87 projects to be loaded for a per-file feature).
Must stay eager: the analyzer rebind **relative to the first compilation** — the comment at
`WorkspaceService.cs:1391-1393` is load-bearing and still correct:

> *"Rebind BEFORE any `GetCompilationAsync` (the framework probe below triggers one): Roslyn's
> default loader opens the original analyzer DLL via PEReader on first compilation access, locking
> it on disk — a rebind after that is too late."*

So the rebind cannot simply be made lazy; it can only be moved off the gate or narrowed in what it
copies. (Interesting corollary: if the framework probe stops forcing a compilation (O4), the rebind
loses its only forcing function at load time and *could* then genuinely be deferred to first
compilation — but that would need a hook that runs before Roslyn's first `GetMetadata()`.)

**Q6 — is phantom-diagnostics fixable at the source?**
Yes, and better than expected. Three signals exist and all three are currently discarded:
(1) `project.ProjectReferences.Any(r => solution.GetProject(r.ProjectId) is null)` — the dangling
reference Roslyn 5.6.0 provably leaves behind for an unresolvable `<ProjectReference>` (§1.8);
(2) an empty project (`ProjectFileInfo.CreateEmpty`) for a project whose evaluation failed;
(3) the framework probe's own verdict, which the code already computes and then discards
(`WorkspaceService.cs:1558-1560`). The `WorkspaceDiagnostic` stream (`:314-318`) is worth keeping
for the human-readable *reason* but is a poor detector — those diagnostics carry no `ProjectId`
(§6), so correlating them means parsing paths out of message text.

**Q7 — memory.**
Ranked by my confidence that it matters at 87 projects: (1) forced compilations for every project
(§1.5) — the syntax trees of the entire codebase, materialized at load, for a question that needed
none of them; (2) duplicate workspaces from the fallback path (§1.7) — the same projects held twice,
with `MaxCachedWorkspaces = 4` meaning four such solutions can coexist; (3) shadow copies of whole
directories (§1.6), on disk rather than in RAM but multiplied by generation counter; (4)
`FileSystemWatcher`s — one per analyzer source dir (`ShadowCopyManager.cs:190-199`) plus one
**recursive** watcher per project that has ever been asked for an ASPX/Razor/proto/resx index
(`ProjectIndexCacheService.cs:353-358`, `IncludeSubdirectories = true`). The latter is bounded by
demand, not by project count, so it is only a problem on a WebForms/proto-heavy solution.

---

## 3. Proposed optimisations

Impact is my estimate unless it says "measured" (nothing here is measured — I did not run the
suite or profile).

| # | What | Where | Impact | Risk | Observable behaviour change |
|---|------|-------|--------|------|------------------------------|
| **O1** | Warm in dependency-root order (projects nothing references first) instead of `.sln` order, so each `OpenProjectAsync` pulls a whole closure. The graph helper already exists: `ProjectReferencesOf`/`Consumers` read it straight from csproj XML. | `Tools/SolutionSessionTool.cs:143-164`; reuse `Languages/Proto/Core/ProtoReferenceService.cs:753-829` | **Large** — collapses ~N loads into ~R opens, R = number of graph roots (typically 3–8). Each avoided load avoids a gate acquisition, a restore probe, a post-open pipeline pass **and a BuildHost spawn/teardown plus a cold MSBuild `ProjectCollection`** (§1.1a) | Low — pure ordering; the final loaded set is identical | Yes, positively: far fewer "Incrementally loaded" lines; progress becomes coarser-grained |
| **O13** | For a solution-keyed cache entry, do the *initial* load with `MSBuildWorkspace.OpenSolutionAsync(sln)` instead of `OpenProjectAsync(seed)`. Keep `EnsureProjectLoadedAsync` only as the repair path for projects the .sln does not list. | `WorkspaceService.cs:502-504`; cache key already is the `.sln` (`:344-346`, `:456`) | **Large** — one `BuildHostProcessManager`, one BuildHost per kind, one MSBuild `ProjectCollection` with warm imports for all N projects, one post-open pipeline pass, one gate acquisition. Note it buys **no parallelism** — Roslyn's loader is sequential (Q2) — the win is purely batch amortisation | Medium — loads all N projects eagerly, which is precisely the memory cost `WorkspacePreloadHostedService.cs:79-84` and `DaemonServer.cs:63-64` deliberately avoid. So this belongs **only** on the explicit `open_solution` / configured-`preload` paths, never on the demand-driven per-file path | Yes on `open_solution` (which already intends to load everything, just badly); none elsewhere |
| **O2** | Restore once per solution before the first open (`dotnet restore <sln>`), fall back to per-project on failure or for loose projects; dedupe in-flight restores in a `ConcurrentDictionary<string,Task>`; in all cases run restore **before** taking the gate. | `WorkspaceService.cs:482`, `:685`, `:1484-1543` | **Large** — removes up to N serialized subprocess launches from the critical path | Medium — `dotnet restore <sln>` mishandles packages.config projects; needs the fallback. Existence-only freshness check remains wrong either way | Fewer "running dotnet restore" lines; one longer restore at open |
| **O3** | Narrow `LoadGate` to just `ws.OpenProjectAsync` + the mapping update. Move restore before it (O2) and the compilation probe out of it (O4). | `WorkspaceService.cs:675-716` | **Medium–Large**, but only *after* O2/O4 — those are what currently occupies the gate | Medium — must keep the gate around the whole `OpenProjectAsync`; the doc at `:1708-1711` is correct and the read-modify-write it protects is real | No |
| **O4** | Replace the framework probe's `project.GetCompilationAsync` with a references-only probe (`CSharpCompilation.Create("probe", references: project.MetadataReferences)`), or short-circuit when a corlib-shaped reference is present. | `WorkspaceService.cs:1555-1560` | **Large** on cold load and on memory — stops parsing the whole codebase to ask a metadata question | Low–Medium — same verdict, no document parsing. Caveat: the compilation is not *wasted* for projects the user later opens, only for the ~80 they never touch | No |
| **O5** | Move the shadow copy off the gate; narrow it from "every `.dll`/`.pdb`/`.json` in the directory" to the analyzer plus its declared dependency closure. | `WorkspaceService.cs:1456`; `ShadowCopyManager.cs:143-181`, `:65-72` | Small–Medium; scales with the number of *distinct* non-NuGet analyzer directories, which is small on SDK solutions and large on packages.config ones | Medium — the whole-directory copy is load-bearing for generators whose deps sit beside them; the *safe* subset of this change is "move it off the gate" only | No |
| **O6** | Delete the dead `RequiresMsBuild(sln)` call (nothing reads `isLegacy`); memoize `GetProjectsFromSolution` against the `.sln` mtime; move owner resolution outside `s_cacheLock`. | `WorkspaceService.cs:364`, `:1107`, `:1132`; `PathHelper.cs:65-99`, `:187-218` | **Medium** — removes ~15k file opens from a cold load and stops the process-wide lock being held across disk I/O (which is what makes an interactive hover queue behind a load) | Low — `isLegacy` is provably unread by both callers | No |
| **O7** | Stop the standalone-fallback workspace from stealing the solution's project mappings: make `RegisterProjectMappingsLocked` first-mapping-wins for the *closure* entries (the explicitly requested path may still be claimed). Better: record the project as failed-in-this-solution and surface it rather than opening a second workspace. | `WorkspaceService.cs:419-423`, `:1058-1067` | **Large when it fires** — eliminates duplicate design-time builds, duplicate memory, the split-solution correctness bug, and the most likely cause of the `AssemblyReference.cache … used by another process` error | Medium — changes failure behaviour; a project that genuinely cannot join the solution workspace now fails visibly instead of silently working in a second one | Yes: a project that used to "work" in a private workspace now reports a load failure. That is the honest answer |
| **O8** | Track per-project load health **structurally**: degraded ⇔ `project.ProjectReferences.Any(r => solution.GetProject(r.ProjectId) is null)` (dangling reference — see §1.8) **or** the project came back empty (`ProjectFileInfo.CreateEmpty`) **or** the framework probe fired. Use the `WorkspaceDiagnostic` stream only for the human-readable reason, not for detection. Report **one** project-level diagnostic instead of the CS0012/CS1061 wall. | `WorkspaceService.cs:314-318`, `:1549-1560`; consumers at `Lsp/Handlers/DiagnosticsHandler.cs:107-114` and `Lsp/Handlers/WorkspaceDiagnosticsHandler.cs:223-238` | Large for perceived quality; ~zero for time | Low–Medium — the structural checks are exact and cheap, and cannot be fooled by a benign warning such as "will not be pruned". The residual risk is hiding a *genuine* error in a project that is also degraded, which is the right trade | Yes, deliberately |
| **O9** | `FindContainingProjectAsync`: answer from the file system / `ProjectEvaluationService.TryGetCached` when unambiguous; load only when it genuinely is not. | `WorkspaceService.cs:725-773`, esp. `:742-751` | Medium — removes a load-to-classify from the hottest path in the product | Medium — linked files (`<Compile Include="..\Other\X.cs" Link="…"/>`) and glob exclusions must not be mis-attributed; needs the evaluation, not a directory guess | No, if done correctly |
| **O10** | Give incremental loads a `ProgressReporter` scope (they have none today; contrast `WorkspaceService.cs:458-459`). | `WorkspaceService.cs:664-718` | None on time; large on perceived responsiveness during the minutes-long tail | Low | Yes: progress appears where there was silence |
| **O11** | Proto budget contract — four separate fixes, see §3.1 | `Languages/Proto/Core/ProtoReferenceService.cs`, `Lsp/Handlers/CodeLensHandler.cs:174`, `Languages/Abstractions/LspContributors.cs:124-127` | Medium | Low–Medium | Partly |
| **O12** | Memory: land O4 and O7 first (they are the two large items). Then consider collapsing `ProjectIndexCacheService`'s per-project recursive watchers into one solution-root watcher. | `ProjectIndexCacheService.cs:353-358` | Small–Medium, and only on markup-heavy solutions | Medium — one watcher means demultiplexing events to entries by path prefix | No |

### 3.1 O11 in detail — judging the new `budget` design

The direction is right: making consumer preloading opt-in is correct, and the reasoning in the
`SearchScopeAsync` remarks (`ProtoReferenceService.cs:637-650`) is sound. Four problems remain.

**(a) The code and its own doc disagree.** The `budget` parameter doc says
(`ProtoReferenceService.cs:652-655`):

> *"`TimeSpan.Zero` starts the load and returns immediately, which is what every incidental caller
> wants."*

The code says the opposite (`:665`):

```csharp
if (budget is { } wait && wait > TimeSpan.Zero)
```

`TimeSpan.Zero` fails `> TimeSpan.Zero`, so the `GetOrAdd` that *starts* the load (`:670-671`) is
skipped entirely. The inline comment at `:662-664` ("Only a caller that asked for it starts the load
at all") describes the actual behaviour, and contradicts the parameter doc directly above it. This
matters beyond tidiness: the remarks promise *"The load still happens, in the background… a second
one, moments later, that is complete"* (`:641-644`, `:646-650`) — with the code as written, for a
zero/absent budget **no background load is ever started**, so the second, complete answer never
arrives unless some other caller supplies a budget. Pick one behaviour and make the docs match.

**(b) The C# code lens still reaches the 15-second budget.** Chain in §1.9b. The fix needs an
"incidental" signal on `ILanguageReferenceContributor.ReferencesAsync`
(`Languages/Abstractions/LspContributors.cs:124-127`), which has no such parameter today, and for
`CodeLensHandler.ResolveAsync` (`:174`) to pass it.

**(c) `s_consumers` outlives the workspace it loaded into.** It is a process-lifetime
`ConcurrentDictionary<string, Task>` (`ProtoReferenceService.cs:700-701`) memoizing "consumers
loaded". But the workspace is evicted after 10 idle minutes (`WorkspaceService.cs:20`) or by the
4-entry LRU cap (`:62-64`). After eviction the memo still says "loaded", so the consumers are never
re-loaded and the search silently narrows for the rest of the process. It should be invalidated
alongside the workspace entry, or keyed on the entry's identity.

**(d) Do *not* parallelise `LoadConsumersAsync` yet.** The sequential `foreach` at `:713-731` looks
like an easy win, but every one of those `GetOrOpenProjectAsync` calls funnels into
`EnsureProjectLoadedAsync` and blocks on the *same* per-entry `LoadGate`
(`WorkspaceService.cs:675`). Parallelising today buys nothing and adds non-determinism (a consumer's
own load may transitively cover later iterations, so the order changes how many opens happen). The
correct sequence is: O1/O3 first, then bound it with `Parallel.ForEachAsync` at
`~Environment.ProcessorCount / 2` (matching `ProjectEvaluationService.cs:56` and
`WorkspaceDiagnosticsHandler.cs:55`). Better still, apply the O1 trick here too — load the
dependency *roots* among the consumers, not all of them; `Consumers()` already computes the reachable
set, and the roots of that set cover it in far fewer opens.

---

## 4. Do these first (ordered)

1. **O6 — delete the dead `isLegacy` computation and get disk I/O out of `s_cacheLock`.**
   Provably safe, self-contained, and it is the one change that also improves *interactive* latency
   while a load is running.
2. **O1 — warm in dependency-root order** (and, on the explicit `open_solution` path only,
   **O13 — one `OpenSolutionAsync`**). Biggest structural win per line changed: both attack the same
   thing, the *number* of top-level Roslyn load calls, which is what multiplies the BuildHost spawns
   and the cold MSBuild import parsing (§1.1a). O1 touches no invariant and helps every path; O13 is
   strictly better on `open_solution` but must not leak onto the demand-driven path.
3. **O2 — solution-level restore, hoisted out of the gate.** The other half of the subprocess bill.
4. **O4 — cheap framework probe.** Removes a full parse of the codebase from load, and (if the
   retention hypothesis in §1.5 holds) is also the biggest memory win.
5. **O3 — narrow the gate.** Do this *after* 3 and 4, because they are what currently occupies it;
   doing it first would just expose an empty win.
6. **O7 — stop the fallback workspace stealing mappings.** Correctness-first, but it also removes
   the duplicate-MSBuild cost and the most plausible cause of the observed `AssemblyReference.cache`
   error.
7. **O8 — degraded-project detection and diagnostic suppression.** This is what makes the whole
   thing *look* fixed to a user.
8. **O11a/b/c — the proto budget contract.** Small, but (b) is a live "scrolling loads the solution"
   path that the recent change was specifically meant to close.
9. Then O9, O5, O10, O11d, O12.

---

## 5. Checked and found already optimal — do not re-investigate

* **`ProjectClassifier.Classify(string)`** (`Services/ProjectClassifier.cs:96-122`, `:229-299`) —
  one forward-only `XmlReader` pass over the csproj, cached against its mtime, never loads a
  workspace. The two-entry-point design (`:87-93`) is deliberate and correct.
* **`ProjectEvaluationService`** (`Services/ProjectModel/ProjectEvaluationService.cs`) — private
  `ProjectCollection` unloaded after each evaluation (`:154`, `:195`), cache keyed on the project
  *and every file it imports* (`:160-162`, `:127-142`), bounded concurrency gate (`:56`), and a
  non-blocking `TryGetCached` for interactive callers (`:70-76`) whose rationale (`:63-69`) is
  exactly right. Captures a fixed property list rather than all ~2000 (`:252-263`). Nothing to do.
* **`WorkspaceAnalyzerLoaderRegistry`** (`Services/WorkspaceAnalyzerLoaderRegistry.cs`) — one
  collectible ALC per analyzer *source directory*, shared process-wide, refcounted by lease,
  unloaded when the last lease drops. Correct and already minimal.
* **`ShadowCopyManager` sharing** — `_shadowDirectories` (`:27`, `:145-146`) means N projects
  sharing one generator pay one copy. Only the *scope* of each copy and *where it runs* are issues
  (O5), not the sharing.
* **`AnalyzerService` / `AnalyzerHost`** — analyzers are instantiated lazily, per project, on first
  diagnostics request (`Services/AnalyzerService.cs:56-65`), not at load; evicted with the owning
  workspace entry (`WorkspaceService.cs:1049-1050`). Not on the load path at all.
* **`WorkspaceDiagnosticsHandler`** — default scope is `openProjects` + dependents, not the whole
  solution (`Config/LspFeatureOptions.cs:30-36`; `WorkspaceDiagnosticsHandler.cs:78-100`); analyzers
  are read **cache-only** (`:227-228`); parallelism is bounded at `ProcessorCount/2` (`:55`);
  pack interest is decided by a metadata lookup cached against the *compilation*
  (`:185-198`). All correct. The one caveat is that `scope == "solution"` (`:81-82`) forces a
  compilation for every project — which is fine as an explicit opt-in, but should not be the default
  on an 87-project solution, and it is not.
* **LSP startup does not eagerly load** — `Initialize` only binds the solution path
  (`WorkspaceService.cs:864-868`); `DaemonServer.cs:63-64` explicitly declines to warm; preload is
  opt-in and documents why (`WorkspacePreloadHostedService.cs:79-84`). The earlier "auto-warm the
  nearest solution" behaviour was already removed for exactly this reason.
* **`PathHelper.GetProjectsFromSolution`, `WorkspaceService.FindReferencingProjects`,
  `ProtoReferenceService.Consumers`** — all three are pure text/XML reads and load nothing
  themselves. The loads always happen at the caller. (`GetProjectsFromSolution` is nonetheless worth
  memoizing — O6 — because it is re-read on every cache miss.)
* **`s_inflight` coalescing** (`WorkspaceService.cs:35`, `:379-388`, `:567`) — sibling requests for
  the same solution correctly collapse onto one load, and waiters respect their own cancellation
  token (`:433`, with the reasoning at `:428-430`). Correct.
* **`ApplyOpenDocumentOverlay` memoization** (`WorkspaceService.cs:1220-1260`) — memoized per store
  generation *and* base solution, with the base-solution check specifically there to catch
  incremental project adds (`:1230-1232`). Correct.
* **Solution Explorer root/folder/file listing** — `.sln` parse and `ProjectEvaluationService` only,
  no Roslyn. The single exception is the Dependencies node (§1.9a).
* **`EvictExpiredEntries` / `TryEvictLoggedLocked`** (`WorkspaceService.cs:1262-1328`) — the
  exception isolation there is deliberate and the comment explaining it (`:1264-1267`) describes a
  real crash. Leave it alone.

---

## 6. Roslyn 5.6.0 facts this analysis rests on

Verified against `Microsoft.CodeAnalysis.Workspaces.MSBuild` **5.6.0**
(`RoslynMCP/RoslynMCP.csproj:27-33`), package at
`C:\Users\Gerard\.nuget\packages\microsoft.codeanalysis.workspaces.msbuild\5.6.0\`, source commit
`c0573ed0a7dc3e3b4d2e70da47f97cc51a35524f` (from the nuspec's `<repository>` element):

* `OpenSolutionAsync` and `OpenProjectAsync` are **strictly sequential** —
  `MSBuildProjectLoader.Worker.LoadAsync` is a plain `foreach`, and `Worker_ResolveReferences`
  recurses with plain `await`s. No parallelism, no knob.
* A **new `BuildHostProcessManager` per top-level open call**, disposed when the call returns; at
  most one BuildHost process per kind within a call. This is the basis of §1.1a.
* `OpenProjectAsync` pulls in transitive `ProjectReference`s by default (its own XML doc: *"Open a
  project file and all referenced projects"*), which confirms the design comment at
  `WorkspaceService.cs:496-501`. `LoadMetadataForReferencedProjects` (default `false`) gates this for
  `OpenProjectAsync` only; `LoadSolutionInfoAsync` hardcodes it off.
* An unresolvable `<ProjectReference>` yields a **dangling `ProjectReference`** to a `ProjectId`
  with no `ProjectInfo`; a project that fails MSBuild evaluation is added as
  `ProjectFileInfo.CreateEmpty`. Both are detectable structurally (§1.8, O8).
* The `WorkspaceDiagnostic` raised for both cases carries **no `ProjectId`**.
* `MSBuildWorkspace` never runs restore (dotnet/roslyn#52293 is still open).

## 7. What I could not verify in this pass

* Whether Roslyn retains the compilations created by the framework probe (§1.5) for the life of the
  workspace's current-solution snapshot. This determines whether O4 is a large *memory* win or only
  a large *time* win. It is a large time win either way.
* Whether a `ProjectReference` to a `ProjectId` absent from the solution is silently excluded from
  the compilation (my assumption) rather than throwing. This is what makes the phantom CS0012 the
  observed symptom rather than a hard failure, and it matches the reported behaviour, but I did not
  trace `SolutionState`/`CompilationTracker` to confirm it for 5.6.0.
* **No timings were measured.** Every "Large/Medium/Small" above is reasoned from the call graph,
  not profiled. Before landing O1–O4 it is worth putting a stopwatch around the three components —
  restore, `OpenProjectAsync`, post-open pipeline — and logging them per project. That costs almost
  nothing, makes the improvement provable, and would immediately confirm or refute the claim in
  §1.1a that BuildHost churn dominates.

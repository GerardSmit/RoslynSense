# RoslynSense — notes for agents

## Never keep a Roslyn snapshot in long-lived state

A `Document`, `Project`, `Solution`, `Compilation`, `SemanticModel`, `SyntaxTree`, `ISymbol`, or
`Diagnostic` is a **snapshot**. Holding any of them keeps the whole solution snapshot alive, with
every compilation it has built: symbols reference their compilation, and a `Diagnostic` carries
symbols as message arguments. On 2026-09-18 the daemon reached 14 GB and two busy cores from this
alone (30 snapshots, 918 compilations, 5,000 queued tasks). Do not reintroduce it.

The rules, each with the shape that caused a real leak:

1. **Never capture a snapshot object into queued or gated work.** A `Task.Run` that awaits a
   `SemaphoreSlim` before using a captured `Document` pins that snapshot for as long as it waits,
   then computes over a version that has moved on. Capture `(Workspace, DocumentId)` and resolve
   through `LiveSnapshot.Document(...)` / `LiveSnapshot.Project(...)` once the slot is taken.
   Immediate fire-and-forget work (no gate) may hold a snapshot for its own duration.
2. **A cache keyed by `DocumentId` or `ProjectId` must have an `EvictProjects(IEnumerable<ProjectId>)`**
   and be called from `WorkspaceService.EvictEntryLocked`. A solution reload creates new ids; the old
   entries are never asked for again and never fall out of an LRU either, because only live entries
   get touched. Existing examples: `AnalyzerDiagnosticCache`, `CompilerDiagnosticCache`,
   `ProjectWideDiagnosticCache`, `DbmlGeneratedIndex`, `ProtoGeneratedIndex`.
3. **A memo key that needs snapshot identity uses `WeakGeneration`**, not the object. Reference
   equality is right ("same compilation as last time"), a strong reference is not. Version stamps
   (`GetDependentSemanticVersionAsync`, `VersionStamp`) are better still when they describe the
   inputs fully — see `DocumentSemanticGeneration`.
4. **Static holders of snapshot payloads are weak or bounded and idle-expiring**: `ConditionalWeakTable`
   keyed on the snapshot object, `WeakReference<T>`, or `WeakSnapshotCache` (registered with
   `MemoryCacheRegistry`, which trims on a timer). A plain static `ConcurrentDictionary<..., X>` where
   `X` transitively holds a snapshot is a leak until proven otherwise.

Before adding a field, record, or closure that holds one of these types, ask: what removes it when
the solution reloads, and what stops it pinning an old snapshot while it waits?

## Verifying memory on the live daemon

Do not reason about a big host from source. The installed debug worker walks its heap in place:

```
~/.dotnet/tools/.store/roslynsense/<ver>/roslynsense/<ver>/tools/net10.0/any/workers/x64/RoslynMCP.DebugWorker.exe --heap-snapshot <pid>
... --heap-roots <pid> CSharpCompilation 10
```

Per-type sizes come from the first; root chains from the second, and every `RoslynMCP` frame in a
chain names one of our holders. `dotnet-stack report -p <pid>` shows what the busy threads run. The
daemon's own log is `%TEMP%\roslyn-mcp-daemon\<hash>\host.log`, with `host.json` beside it naming the
solution and pid.

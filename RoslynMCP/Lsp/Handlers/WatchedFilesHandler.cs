using RoslynMCP.Languages;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>
/// workspace/didChangeWatchedFiles: keeps the loaded workspace honest about changes made
/// outside the editor — git checkouts, scaffolding, another agent's edits. Without this the
/// server answers from a stale snapshot until someone manually reloads, and the staleness
/// gets blamed on the language server.
///
/// Events are coalesced before acting: a branch switch fires hundreds at once and each one
/// would otherwise trigger its own reload.
/// </summary>
internal static class WatchedFilesHandler
{
    private static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long a burst may hold off processing before it is flushed anyway.
    /// </summary>
    /// <remarks>
    /// The quiet window alone is a trailing debounce: every new event restarts it, so a process
    /// that keeps writing — an agent editing a hundred files, a long checkout, a code generator —
    /// holds it off indefinitely and the workspace stays stale for as long as the writing lasts.
    /// This bounds that, so a sustained stream is handled in periodic batches instead of one batch
    /// after everything finally stops.
    /// </remarks>
    private static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Serializes the flushes themselves.
    /// </summary>
    /// <remarks>
    /// The debounce token only guards the delay, and once <see cref="MaximumWait"/> forces an
    /// immediate flush there is no delay to cancel — so a burst could have two batches applying at
    /// once. Collapse only orders events within a batch, so a delete from the first could land
    /// after a create from the second for the same file, leaving the document removed for a file
    /// that exists and nothing to ever re-drive it.
    /// </remarks>
    private static readonly SemaphoreSlim s_flushGate = new(1, 1);

    private static readonly object s_gate = new();
    private static readonly List<FileEvent> s_pending = [];
    private static readonly Services.Debouncer s_debounce = new("Lsp");
    private static DateTime s_firstPendingUtc;

    /// <summary>What a batch of events did — returned for tests and logging.</summary>
    internal sealed record Outcome(
        bool ReloadedWorkspace,
        IReadOnlyList<string> EvictedProjects,
        IReadOnlyList<string>? InvalidatedMarkup = null,
        IReadOnlyList<string>? AppliedDocumentChanges = null)
    {
        public bool DidAnything =>
            ReloadedWorkspace
            || EvictedProjects.Count > 0
            || InvalidatedMarkup is { Count: > 0 }
            || AppliedDocumentChanges is { Count: > 0 };
    }

    public static void Handle(DidChangeWatchedFilesParams p)
    {
        TimeSpan delay;
        lock (s_gate)
        {
            if (s_pending.Count == 0)
                s_firstPendingUtc = DateTime.UtcNow;

            s_pending.AddRange(p.Changes);
            delay = DateTime.UtcNow - s_firstPendingUtc >= MaximumWait ? TimeSpan.Zero : Coalesce;
        }

        s_debounce.Restart(delay, FlushAsync);
    }

    private static async Task FlushAsync(CancellationToken ct)
    {
        try
        {
            FileEvent[] batch;
            lock (s_gate)
            {
                batch = Collapse(s_pending);
                s_pending.Clear();
                s_firstPendingUtc = DateTime.UtcNow;
            }

            Outcome outcome;
            await s_flushGate.WaitAsync();
            try
            {
                outcome = await ProcessAsync(batch, CancellationToken.None);
            }
            finally
            {
                s_flushGate.Release();
            }

            // Coalesced: several batches can land in a row during a checkout, and a refresh costs
            // the client a re-pull of every open document plus a workspace sweep.
            if (outcome.DidAnything)
                LspSessionRegistry.ScheduleRefresh(RefreshKind.All, "watched-files");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Lsp] Watched-file processing failed: {ex.Message}");
        }
    }

    /// <summary>
    /// One event per file: the last thing that happened to it.
    /// </summary>
    /// <remarks>
    /// A tool that rewrites a file several times — a formatter, a code generator, an agent working
    /// through a change — produces an event per write, and each one used to be processed on its
    /// own. Collapsing first means the work is proportional to the number of files touched rather
    /// than the number of writes.
    /// </remarks>
    internal static FileEvent[] Collapse(IReadOnlyList<FileEvent> events)
    {
        var byPath = new Dictionary<string, FileEvent>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in events)
        {
            // Keyed on the resolved path rather than the URI text: two clients can encode the same
            // file differently (an escaped drive letter, say) and would otherwise not collapse.
            string key = LspConverters.UriToPath(e.Uri);

            // Last one wins. Order is the only thing that distinguishes a file being replaced —
            // which many writers do by unlinking and recreating, or renaming a temporary over the
            // top — from one being deleted for good. Ranking by severity instead made every such
            // save look like a delete: the document was dropped from the project, every type it
            // declared went unresolved solution-wide, and nothing ever put it back.
            byPath[key] = e;
        }

        return [.. byPath.Values];
    }

    /// <summary>The whole decision, without the debounce — the unit under test.</summary>
    internal static async Task<Outcome> ProcessAsync(
        IReadOnlyList<FileEvent> changes, CancellationToken ct)
    {
        var events = changes
            .Select(c => (Path: LspConverters.UriToPath(c.Uri), Change: KindOf(c.Type)))
            .Where(e => !IsIgnored(e.Path))
            // The echo of our own writes. Every mutating operation invalidates what it changed and
            // then writes a .sln or .csproj; without this the watcher reports that write back and
            // the whole workspace is evicted a second time, for a change already accounted for.
            .Where(e => !SelfWriteTracker.WasWrittenByUs(e.Path))
            .ToList();
        var paths = events.Select(e => e.Path).ToList();

        if (paths.Count == 0)
            return new Outcome(false, []);

        // A source or a credential changed. That needs the feed configuration reloaded, not the
        // workspace — and on its own it is not a reason to reload anything else.
        if (paths.Any(IsNuGetConfig))
        {
            Services.Packages.NuGetFeedContext.Invalidate();
            Services.Packages.PackageUpdateService.Invalidate();

            // The project-file squiggles too: they distinguish "no such version" from "no feed
            // answered", and that distinction is exactly what a config change moves. Fixing a
            // credential has to be able to clear the errors it was causing.
            Languages.MsBuild.Core.PackageStatusCache.Invalidate();
        }

        // A pack's file changed under us. Which paths those are, and what has to be dropped for
        // one, is the pack's own business — a web.config is not markup but decides how every page
        // in the project binds — so each path is offered to every pack rather than to the first
        // one that claims it.
        //
        // The registered packs and not any one connection's enabled set: the caches behind this
        // are process-wide, the debounce below batches events from every window at once, and a
        // window that switched a pack off must not leave another window's editor reporting on
        // markup that has since changed on disk.
        var watchers = LanguageScope.Process.Contributors<ILanguageWatchedFileHandler>();
        var invalidatedMarkup = new List<string>();
        foreach (var (path, change) in events)
        {
            bool invalidated = false;
            foreach (var watcher in watchers)
                invalidated |= watcher.Invalidate(path, change);

            if (invalidated)
                invalidatedMarkup.Add(path);
        }

        // Analyzer configuration changed: severities and analyzer options are baked into the loaded
        // project, and every cached analyzer result was computed under the old rules. An
        // .editorconfig also applies to a whole directory tree rather than to one project, so this
        // is the one case where dropping everything is the honest answer.
        if (paths.Any(IsAnalyzerConfig))
        {
            await using var progress = await ProgressReporter.BeginAsync("Reloading workspace", ct);
            AnalyzerDiagnosticCache.Clear();
            ProjectWideDiagnosticCache.Clear();
            await WorkspaceService.EvictAllAsync(ct);
            return new Outcome(true, []);
        }

        // A project-shaping file changed: references, analyzers and compile items all come from
        // MSBuild evaluation, so nothing short of a reload is correct for the projects it shapes.
        // Which projects those are is the question that used to be skipped — a single .csproj
        // reloaded every solution the process had open, including ones in other windows that
        // shared nothing with it. A .sln or an imported .props reaches further than one project,
        // so those still take everything.
        var projectFiles = paths.Where(IsProjectFile).ToList();
        var reachesEverything = paths.Any(p => IsProjectShaping(p) && !IsProjectFile(p));

        if (reachesEverything)
        {
            await using var progress = await ProgressReporter.BeginAsync("Reloading workspace", ct);
            await WorkspaceService.EvictAllAsync(ct);
            return new Outcome(true, []);
        }

        bool reloadedProjects = false;
        if (projectFiles.Count > 0)
        {
            await using var progress = await ProgressReporter.BeginAsync("Reloading projects", ct);

            foreach (string projectFile in projectFiles)
                await WorkspaceService.EvictProjectAsync(projectFile, ct);

            // No analyzer-cache clear: its entries are keyed by DocumentId, and the reload gives
            // this project's documents new ids, so its stale results age out on their own while
            // every other project's stay valid.
            //
            // Deliberately falling through rather than returning. One batch can carry a .csproj
            // from one solution and source edits from another — the daemon serves several at once,
            // and a checkout touches whatever it touches. Returning here dropped every source
            // event that happened to arrive alongside a project file, leaving those workspaces on
            // the pre-checkout text with nothing to correct them.
            reloadedProjects = true;
        }

        // Source files added or removed on disk: the owning project's document set is wrong.
        // Applied to the live workspace where that is sound, because eviction is not the local
        // operation its name suggests — one cache entry serves a whole solution, so evicting "just
        // this project" discards every compilation and analyzer result in the solution. A branch
        // switch went through here.
        var evicted = new List<string>();
        var applied = new List<string>();
        var warm = new List<string>();
        foreach (var (path, change) in events.Where(e => IsSourceFile(e.Path)))
        {
            var kind = change switch
            {
                WatchedFileChange.Created => FileChange.Created,
                WatchedFileChange.Deleted => FileChange.Deleted,
                _ => FileChange.Changed,
            };

            foreach (var projectPath in FindNearestProjectFiles(path))
            {
                var result = await WorkspaceService.TryApplyFileChangeAsync(projectPath, path, kind, ct);

                switch (result)
                {
                    case FileSyncResult.Applied:
                        applied.Add(path);
                        // Not for a delete. There is no document left to resolve, and resolving a
                        // path nothing owns is a walk up the directory tree that opens every
                        // project it passes — a workspace load, off any request, for a file that
                        // just stopped existing. Removing the document already moved the
                        // project's checksum, and Roslyn's post-request refresh brings the index
                        // current the next time completion asks.
                        if (kind != FileChange.Deleted)
                            warm.Add(path);
                        break;

                    // Nothing moved, so nothing downstream is stale. Counting this as work is how
                    // a formatter or an agent writing files bought a full workspace re-pull for a
                    // change the server had already accounted for.
                    case FileSyncResult.NothingToDo:
                        break;

                    case FileSyncResult.CannotApply:
                        // Once per project, not once per file: a legacy project with fifty new
                        // files in one checkout would otherwise take fifty sequential reloads of
                        // the same workspace.
                        if (!evicted.Contains(projectPath, StringComparer.OrdinalIgnoreCase))
                        {
                            await WorkspaceService.EvictProjectAsync(projectPath, ct);
                            evicted.Add(projectPath);
                        }
                        break;
                }
            }
        }

        // No analyzer-cache clear here, and deliberately so: eviction reloads the solution under
        // fresh ids, so every cached result for it is keyed by a DocumentId nothing will ever ask
        // for again and ages out of the cap on its own. Clearing would also drop every other
        // solution's still-valid results.

        // A document updated in place invalidated its project's import-completion index exactly
        // like a keystroke would — a regenerated designer file (.dbml, .aspx) is the common case
        // — and completion no longer rebuilds that index on its own thread. Immediate, because
        // these arrive post-save behind the coalesce window; nobody is mid-keystroke.
        foreach (string path in warm)
            ImportCompletionWarmer.Schedule(path, immediate: true);

        return new Outcome(reloadedProjects, [.. evicted, .. projectFiles], invalidatedMarkup, applied);
    }

    private static WatchedFileChange KindOf(int type) => type switch
    {
        FileChangeType.Created => WatchedFileChange.Created,
        FileChangeType.Deleted => WatchedFileChange.Deleted,
        _ => WatchedFileChange.Changed,
    };

    private static bool IsIgnored(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(segment =>
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsProjectShaping(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is
            ".csproj" or ".vbproj" or ".fsproj" or ".props" or ".targets" or ".sln" or ".slnx" or ".slnf";

    /// <summary>
    /// A project file, as opposed to something a project file imports or lists. The distinction is
    /// how far the reload has to reach: a <c>.csproj</c> shapes one project, while an imported
    /// <c>.props</c> or a <c>.sln</c> can shape every project that sees it.
    /// </summary>
    private static bool IsProjectFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".csproj" or ".vbproj" or ".fsproj";

    private static bool IsNuGetConfig(string path) =>
        Path.GetFileName(path).Equals("nuget.config", StringComparison.OrdinalIgnoreCase);

    private static bool IsAnalyzerConfig(string path)
    {
        string name = Path.GetFileName(path);
        return name.Equals(".editorconfig", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".globalconfig", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A file the workspace compiles. VB is included for symmetry with the apply path, which
    /// handles it — though the extension currently registers watchers for <c>**/*.cs</c> and the
    /// project globs only, so no <c>.vb</c> event reaches here today.
    /// </summary>
    private static bool IsSourceFile(string path) => IsCompiledSource(path);

    /// <summary>Whether the workspace compiles this file, and so has a document for it.</summary>
    internal static bool IsCompiledSource(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".cs" or ".vb";

    /// <summary>Project files in the nearest ancestor directory that has any. Deliberately a
    /// disk walk rather than <see cref="WorkspaceService.FindContainingProjectAsync"/>: a
    /// just-created file is not in any loaded snapshot yet, which is exactly the case that
    /// needs handling.
    /// All candidates in that directory are returned — one folder can hold several projects,
    /// and picking one arbitrarily would evict the wrong snapshot and leave the stale one.</summary>
    internal static IReadOnlyList<string> FindNearestProjectFiles(string path)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(path) ?? path);
        while (dir is not null)
        {
            if (dir.Exists)
            {
                var projects = dir.EnumerateFiles("*.*proj")
                    .Where(f => f.Extension is ".csproj" or ".vbproj" or ".fsproj")
                    .Select(f => f.FullName)
                    .ToList();
                if (projects.Count > 0)
                    return projects;
            }
            dir = dir.Parent;
        }
        return [];
    }
}

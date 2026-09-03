using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMCP.Config;
using RoslynMCP.Lsp.Search;
using RoslynMCP.Services;

namespace RoslynMCP.Lsp;

/// <summary>
/// Loads every project the bound solution lists, once, in the background, as soon as an editor
/// connects.
/// </summary>
/// <remarks>
/// <para>
/// Everything else in the workspace layer is demand-driven: a project is loaded because a request
/// named a file inside it. That is right for the per-file features — nobody wants thirty projects
/// evaluated so one document can get its squiggles — and wrong for the solution-wide ones, which
/// have no file to be driven by. Search Everywhere is the visible case: it searches
/// <see cref="WorkspaceService.TryGetSessionSolution"/>, so before any file is opened it
/// searches an empty solution and finds nothing, and after one file is opened it finds that
/// project's closure and nothing else. A search box whose answers depend on which tabs happen to
/// be open is not a search box.
/// </para>
/// <para>
/// One batch rather than a project-at-a-time loop, for the reason
/// <see cref="Services.PartialSolution"/> documents: Roslyn's fixed per-call cost dwarfs the
/// per-project one, so the whole solution in one call is several times cheaper than its projects
/// one at a time.
/// </para>
/// <para>
/// Started from <c>initialized</c> rather than from <c>BindSolution</c>: binding happens in every
/// process that touches a solution, including short-lived MCP invocations that want one project,
/// while this is a cost only an editor session earns back. Idempotent per solution because the
/// daemon is shared — a second window connecting must join the first window's load, not start a
/// second one.
/// </para>
/// </remarks>
internal static class SolutionWarmup
{
    private static readonly object s_gate = new();
    private static string? s_solutionPath;
    private static Task s_warm = Task.CompletedTask;

    /// <summary>
    /// Whether the solution is being loaded right now — what tells the Solution Explorer to say
    /// "loading…" over a project rather than "not loaded".
    /// </summary>
    public static bool IsLoading
    {
        get
        {
            lock (s_gate)
                return !s_warm.IsCompleted;
        }
    }

    /// <summary>
    /// Starts the load if it has not been started for this solution, and returns the task that
    /// completes when it is done. Never throws: a solution that will not load leaves the workspace
    /// exactly as demand-driven as it was before.
    /// </summary>
    public static Task Start()
    {
        if (!LspFeatureOptions.LoadEntireSolution)
            return Task.CompletedTask;

        if (WorkspaceService.BoundSolutionPath is not { Length: > 0 } solution)
            return Task.CompletedTask;

        lock (s_gate)
        {
            if (s_solutionPath is not null
                && string.Equals(s_solutionPath, solution, StringComparison.OrdinalIgnoreCase))
            {
                return s_warm;
            }

            s_solutionPath = solution;

            // Before the load, not after it, and never awaited by it: reading the names off disk
            // is what makes the first Ctrl+T answerable during the seconds MSBuild is busy. It
            // needs the solution file and nothing the load produces. See Search.NameIndex.
            _ = NameIndex.Start(solution);

            // Task.Run, not a bare async call: this runs from the initialized notification, and
            // the first thing the load does — reading the solution file — must not sit on the
            // JSON-RPC dispatch thread while the editor is waiting to send its first request.
            s_warm = Task.Run(() => LoadAsync(solution));
            return s_warm;
        }
    }

    /// <summary>
    /// Reloads whatever of the bound solution has been unloaded since the warm-up ran, if
    /// anything has. Cheap and a no-op when the solution is intact.
    /// </summary>
    /// <remarks>
    /// <see cref="Start"/> runs once per solution, which was written for the second editor window
    /// joining the first — and it means an eviction after the warm-up leaves the solution unloaded
    /// forever. Evictions are routine, not exceptional: a build in a terminal changes restore
    /// outputs, the <see cref="Services.RestoreWatcher"/> throws the workspace away so it reloads
    /// with the new graph, and the "reloads" half only ever happened when the next request named a
    /// file in the evicted project. Solution-wide views have no file to ask with, so the Solution
    /// Explorer sat on "not loaded" and Search Everywhere on a shrunken solution until the daemon
    /// was restarted.
    /// <para>
    /// Called from the debounced project-set notification rather than from the eviction itself, so
    /// one build's burst of evictions becomes one reload. The missing-projects check runs before
    /// any state is touched: when the set moved because a load <i>grew</i> it — the common case —
    /// this discovers there is nothing to do and leaves the finished warm-up alone.
    /// </para>
    /// </remarks>
    public static void EnsureLoaded()
    {
        string solution;
        lock (s_gate)
        {
            // No warm-up has run (no editor session, or the feature is off), or one is running
            // right now and will see the current state when it finishes.
            if (s_solutionPath is not { } path || !s_warm.IsCompleted)
                return;
            solution = path;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var projects = PathHelper.GetProjectsFromSolution(solution);
                if (projects.Count == 0)
                    return;

                var missing = await WorkspaceService.ProjectsNotYetLoadedAsync(projects);
                if (missing.Count == 0)
                    return;

                lock (s_gate)
                {
                    if (!s_warm.IsCompleted)
                        return;
                    s_warm = Task.Run(() => LoadAsync(solution));
                }
            }
            catch (Exception ex)
            {
                ServiceLog.Warn(
                    $"Could not reload '{Path.GetFileName(solution)}' after its projects were " +
                    $"unloaded: {ex.Message}. They will load as files in them are opened.",
                    key: $"solution-warmup-rearm:{solution}");
            }
        });
    }

    /// <summary>
    /// Awaits the load a solution-wide request depends on, if one is running.
    /// </summary>
    /// <remarks>
    /// Cancellable by the caller's own token, so a search the user has already retyped past stops
    /// waiting with it. When nothing is loading — the feature is off, or the load already finished
    /// — this returns immediately and the caller behaves exactly as it did before.
    /// </remarks>
    public static async Task WaitAsync(CancellationToken ct)
    {
        Task warm;
        lock (s_gate)
            warm = s_warm;

        if (warm.IsCompleted)
            return;

        await warm.WaitAsync(ct);
    }

    /// <summary>
    /// The load in progress, as a task — for a caller that wants to race it rather than wait for
    /// it. Completed when nothing is loading.
    /// </summary>
    /// <remarks>
    /// What lets a search answer from <see cref="Search.NameIndex"/> only for as long as that is
    /// the better of the two available answers: the moment the real corpus exists, the race is
    /// over and the search takes the real one.
    /// </remarks>
    public static Task Loading
    {
        get
        {
            lock (s_gate)
                return s_warm;
        }
    }

    private static bool s_loadedOnce;

    /// <summary>
    /// Whether the bound solution has finished loading at least once in this daemon.
    /// </summary>
    /// <remarks>
    /// What separates the cold open from every later load. <see cref="Search.NameIndex"/> stands in
    /// for the solution only before the first load lands: after that the workspace exists and is
    /// kept current by the editor, while the index is a snapshot of the disk taken minutes or hours
    /// ago. A reload — <see cref="EnsureLoaded"/> after a build evicted something — must not fall
    /// back to it, because a build is exactly when the files it described have changed, and because
    /// a language pack's claim on a query needs a solution that by then exists.
    /// </remarks>
    public static bool HasLoadedOnce
    {
        get
        {
            lock (s_gate)
                return s_loadedOnce;
        }
    }

    private static async Task LoadAsync(string solutionPath)
    {
        try
        {
            var projects = PathHelper.GetProjectsFromSolution(solutionPath);
            if (projects.Count == 0)
                return;

            // The daemon may already be serving another window, or an open_solution from a chat.
            // Loading what is loaded would throw away that workspace and build it again.
            var missing = await WorkspaceService.ProjectsNotYetLoadedAsync(projects);
            if (missing.Count > 0)
            {
                string name = Path.GetFileNameWithoutExtension(solutionPath);
                await using var progress = await ProgressReporter.BeginAsync(
                    $"Loading {name} ({missing.Count} project{(missing.Count == 1 ? "" : "s")})");

                await WorkspaceService.EnsureProjectsLoadedAsync(missing);
            }
        }
        catch (OperationCanceledException)
        {
            // Server shutting down mid-load.
        }
        catch (Exception ex)
        {
            ServiceLog.Warn(
                $"Could not load every project of '{Path.GetFileName(solutionPath)}': {ex.Message}. " +
                "Projects will load as files in them are opened.",
                key: $"solution-warmup:{solutionPath}");
        }

        lock (s_gate)
            s_loadedOnce = true;

        // The stand-in has done its job and is now only a few megabytes of declarations nobody
        // will read again this session.
        NameIndex.Retire();

        // Whatever a client was told while this was running, it was told provisionally: a search
        // answered from the name index carries "the solution is still loading", and the panel
        // reruns its query when this arrives. Sent whether or not the load threw — a partial
        // solution is still a better corpus than one read off disk, and a client left waiting for
        // a signal that never comes shows a stale banner forever.
        LspSessionRegistry.NotifySolutionReady();

        // Deliberately outside the try and outside anything a caller awaits: a solution that was
        // already loaded still needs this, and a search must never wait for it.
        var warm = Task.Run(WarmSymbolsAsync);
        lock (s_gate)
            s_warmedSymbols = warm;
    }

    private static Task s_warmedSymbols = Task.CompletedTask;

    /// <summary>How many index builds run at once while somebody is waiting on a request — high
    /// enough that a sweep of thousands of documents is not one core's worth of work, low enough
    /// that a keystroke arriving mid-sweep still finds a free core to be answered on. With nothing
    /// in flight the sweep runs wide instead; <see cref="ForegroundGate"/> moves it between the
    /// two.</summary>
    private const int IndexConcurrency = 3;

    /// <summary>The pause taken between two projects' compilations. Long enough that queued
    /// request work reaches a thread before the next multi-hundred-millisecond compile starts,
    /// short enough that eighteen of them are lost in the noise of the compiles themselves.</summary>
    private static readonly TimeSpan Breath = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Builds the caches every solution-wide gesture reads, so the first Ctrl+T, Shift+F12 and
    /// Ctrl+F12 do not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured on an eighteen-project, ~2,600-document solution: the first query cost 7.3 seconds
    /// and the second 0.15. All of that difference is cache construction — enumerating all 78,893
    /// declared names costs 8 milliseconds once the indexes exist, and running the matcher over
    /// every one of them costs 120. It is the compilations, and Roslyn's per-project declaration
    /// index, that are expensive to build and cheap to reuse.
    /// </para>
    /// <para>
    /// Which matters more than the seconds suggest, because the panel cancels its in-flight request
    /// on every keystroke. A cost paid per search is a cost paid per character, and a typist who
    /// stays ahead of it aborts each attempt before it finishes — so a search that is merely slow
    /// reads as a search that finds nothing. That is the bug this removes; the ranking work in the
    /// same area only decides what a completed search returns.
    /// </para>
    /// <para>
    /// Three things decide the shape of the pass. <b>Order:</b> the project the user has files open
    /// in goes first (<see cref="WarmOrder"/>), because every other project's turn is time that
    /// project spends cold — and a cold project is worse than merely slow now that semantics are
    /// served from frozen snapshots, since freezing a project that was never built yields a
    /// near-empty compilation rather than a slow one. <b>Coverage:</b> the declaration sweep at the
    /// end builds only the top-level index; find-references narrows through the *other* per-document
    /// index and go-to-implementation builds a <see cref="SymbolTreeInfo"/> per metadata reference,
    /// so both are swept here or the first of each gesture pays for them inside the user's wait.
    /// <b>Position:</b> the index sweep runs before the compilations, because indexes need only
    /// text checksums — on a session with a warm on-disk store it is nearly free, and it is the
    /// half that the gestures block on hardest.
    /// </para>
    /// <para>
    /// Throttled throughout — bounded concurrency over the sweep, a pause between compilations —
    /// so the background pass never holds every core against the request that arrives while it
    /// runs. That is the cost the reorder buys back: more work happens earlier, so it has to be
    /// work the foreground can interrupt.
    /// </para>
    /// <para>
    /// Unawaited on purpose: blocking the first search until this finishes would trade a slow answer
    /// for no answer at all — a query that races it pays what it would have paid anyway, and
    /// Roslyn's own async caches mean the two never build the same thing twice. Cancellable only by
    /// shutdown, for the same reason: the keystroke that would cancel it is the one that needed it.
    /// </para>
    /// </remarks>
    private static Task WarmSymbolsAsync() => WarmSymbolsAsync(CancellationToken.None);

    /// <summary>Test seam: the warm pass, with a token a test can stop the sweep with.</summary>
    internal static async Task WarmSymbolsAsync(CancellationToken ct)
    {
        try
        {
            if (WorkspaceService.TryGetSessionSolution() is not { } solution)
                return;

            var order = WarmOrder(solution);

            // Before the compilations: see the remarks. Cheap where storage is warm, and the
            // gestures that block on it block on nothing else.
            await SweepIndexesAsync(solution, order, ct);

            foreach (var project in order)
            {
                ct.ThrowIfCancellationRequested();

                await project.GetCompilationAsync(ct);

                // With the compilation in hand this is just the type walk — build each project's
                // import-completion index now, so the first Ctrl+Space anywhere in the solution
                // gets unimported types without having to wait for (or miss) them.
                ImportCompletionWarmer.Queue(project);

                // Hand the pool back between projects, so a burst of keystrokes that arrives
                // mid-warm is dispatched rather than queued behind the next compilation.
                await Task.Delay(Breath, ct);
            }

            // The never-true-predicate FindSourceDeclarationsAsync that used to sit here primed
            // the declaration search Search Everywhere ran on. That search now reads
            // TopLevelSyntaxTreeIndex directly — built by the index sweep above, before the
            // compilation loop — so the call had nothing left to warm.
        }
        catch (OperationCanceledException)
        {
            // Shutdown, or a test stopping the sweep. Nothing to report: a warm that did not
            // finish leaves exactly the cold-but-correct state it started from.
        }
        catch (Exception ex)
        {
            // Nothing here is required for correctness — a cold search is a slow search, not a
            // wrong one — so a project that will not compile must not take the log with it.
            ServiceLog.Warn(
                $"Could not warm the symbol index: {ex.Message}. Searches will warm it themselves.",
                key: "solution-warmup:symbols");
        }
    }

    /// <summary>
    /// The projects of <paramref name="solution"/>, those holding a document the editor has open
    /// first, solution order preserved within each group.
    /// </summary>
    /// <remarks>
    /// Warm-up work is never wasted no matter what order it happens in — Roslyn advances tracker
    /// state in place and shares it by reference across forks — so this is purely about who waits.
    /// Whatever the user is looking at is what the next request will be about, and until its turn
    /// comes that project answers from an unbuilt compilation.
    /// </remarks>
    internal static IReadOnlyList<Project> WarmOrder(Solution solution)
    {
        var open = OpenDocumentStore.OpenPaths();
        if (open.Count == 0)
            return solution.Projects.ToList();

        var openPaths = new HashSet<string>(open, StringComparer.OrdinalIgnoreCase);

        var edited = new List<Project>();
        var rest = new List<Project>();

        foreach (var project in solution.Projects)
            (HoldsAnOpenDocument(project, openPaths) ? edited : rest).Add(project);

        edited.AddRange(rest);
        return edited;
    }

    /// <remarks>
    /// Additional documents count: an open .aspx or .razor is edited through the project that lists
    /// it just as much as an open .cs is, and it is the same project whose compilation the request
    /// about it will need.
    /// </remarks>
    private static bool HoldsAnOpenDocument(Project project, HashSet<string> openPaths)
    {
        foreach (var document in project.Documents)
        {
            if (document.FilePath is { Length: > 0 } path
                && openPaths.Contains(PathHelper.NormalizePath(path)))
            {
                return true;
            }
        }

        foreach (var document in project.AdditionalDocuments)
        {
            if (document.FilePath is { Length: > 0 } path
                && openPaths.Contains(PathHelper.NormalizePath(path)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds the per-document syntax indexes and the per-metadata-reference symbol trees, at
    /// <see cref="IndexConcurrency"/> at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two indexes per document, not one. The declaration sweep this warm ends with populates the
    /// top-level index only — the names a search box matches. Find-references narrows candidate
    /// documents through the full <see cref="SyntaxTreeIndex"/> (does this file mention this
    /// identifier at all), which is a different table over the same tree, so a first Shift+F12 on a
    /// solution warmed without it still parses every document before it can start searching.
    /// </para>
    /// <para>
    /// Metadata references are swept per distinct file, not per project: a reference to the same
    /// assembly from thirty projects is one <see cref="SymbolTreeInfo"/>, and building it thirty
    /// times would be the sweep's whole cost. These are what go-to-implementation builds on first
    /// use when it looks for types outside the source it can see.
    /// </para>
    /// </remarks>
    /// <param name="swept">
    /// Test seam, called once per indexed document. A counter the caller owns rather than one this
    /// class keeps, so that a test measuring a sweep is not also measuring whatever background warm
    /// an earlier test left running.
    /// </param>
    internal static async Task SweepIndexesAsync(
        Solution solution, IReadOnlyList<Project> order, CancellationToken ct, Action? swept = null)
    {
        var documents = new List<Document>();
        var references = new List<PortableExecutableReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in order)
        {
            if (!project.SupportsCompilation)
                continue;

            documents.AddRange(project.Documents);

            foreach (var reference in project.MetadataReferences.OfType<PortableExecutableReference>())
            {
                if (reference.FilePath is { Length: > 0 } path && seen.Add(path))
                    references.Add(reference);
            }
        }

        // Wide, and narrowed per item by the gate: on a cold open there is nobody to yield to and
        // the sweep should finish as early as it can, while a sweep still running when the user
        // starts typing drops back to IndexConcurrency within one document's work.
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = ct,
        };

        await Parallel.ForEachAsync(documents, options, async (document, token) =>
        {
            using var admitted = await ForegroundGate.AdmitAsync(token).ConfigureAwait(false);
            await SyntaxTreeIndex.GetIndexAsync(document, token).ConfigureAwait(false);
            // Derives TopLevelSyntaxTreeIndex on the way through, and leaves behind the flat
            // per-document entry a Search Everywhere keystroke reads — so the first search after
            // warmup is as warm as the hundredth.
            await LoadedDeclarationCache.GetAsync(document, token).ConfigureAwait(false);
            swept?.Invoke();
        });

        await Parallel.ForEachAsync(references, options, async (reference, token) =>
        {
            using var admitted = await ForegroundGate.AdmitAsync(token).ConfigureAwait(false);
            var checksum = SymbolTreeInfo.GetMetadataChecksum(solution.Services, reference, token);
            await SymbolTreeInfo
                .GetInfoForMetadataReferenceAsync(solution, reference, checksum, token)
                .ConfigureAwait(false);
        });
    }

    /// <summary>Test seam: the background symbol warm, which nothing in the server awaits.</summary>
    internal static Task WarmedSymbols
    {
        get
        {
            lock (s_gate)
                return s_warmedSymbols;
        }
    }

    /// <summary>Test seam: forgets that a solution was warmed, so the next start reloads it.</summary>
    internal static void Reset()
    {
        NameIndex.Reset();

        lock (s_gate)
        {
            s_solutionPath = null;
            s_warm = Task.CompletedTask;
            s_warmedSymbols = Task.CompletedTask;
            s_loadedOnce = false;
        }
    }
}

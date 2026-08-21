using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMCP.Config;
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
/// <see cref="WorkspaceService.TryGetMostRecentSolution"/>, so before any file is opened it
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

            // Task.Run, not a bare async call: this runs from the initialized notification, and
            // the first thing the load does — reading the solution file — must not sit on the
            // JSON-RPC dispatch thread while the editor is waiting to send its first request.
            s_warm = Task.Run(() => LoadAsync(solution));
            return s_warm;
        }
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

        // Deliberately outside the try and outside anything a caller awaits: a solution that was
        // already loaded still needs this, and a search must never wait for it.
        var warm = Task.Run(WarmSymbolsAsync);
        lock (s_gate)
            s_warmedSymbols = warm;
    }

    private static Task s_warmedSymbols = Task.CompletedTask;

    /// <summary>
    /// Builds the two caches every solution-wide search reads, so the first Ctrl+T does not.
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
    /// Uncancellable and unawaited, both on purpose. Cancellable would defeat the point, since the
    /// keystroke that cancels it is the one that needed it. Unawaited because blocking the first
    /// search until this finishes would trade a slow answer for no answer at all: a query that
    /// races it pays what it would have paid anyway, and Roslyn's own async caches mean the two
    /// never build the same thing twice.
    /// </para>
    /// </remarks>
    private static async Task WarmSymbolsAsync()
    {
        try
        {
            if (WorkspaceService.TryGetMostRecentSolution() is not { } solution)
                return;

            foreach (var project in solution.Projects)
            {
                await project.GetCompilationAsync(CancellationToken.None);

                // With the compilation in hand this is just the type walk — build each project's
                // import-completion index now, so the first Ctrl+Space anywhere in the solution
                // gets unimported types without having to wait for (or miss) them.
                ImportCompletionWarmer.Queue(project);
            }

            // A predicate that is never true: this materialises no symbol and builds every index.
            await SymbolFinder.FindSourceDeclarationsAsync(
                solution, _ => false, SymbolFilter.TypeAndMember, CancellationToken.None);
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
        lock (s_gate)
        {
            s_solutionPath = null;
            s_warm = Task.CompletedTask;
            s_warmedSymbols = Task.CompletedTask;
        }
    }
}

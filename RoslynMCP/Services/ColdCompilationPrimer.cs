using Microsoft.CodeAnalysis;

namespace RoslynMCP.Services;

/// <summary>
/// Starts cold dependencies of one immutable project snapshot in parallel. Roslyn otherwise
/// reaches sibling project references sequentially while finalizing the requested compilation.
/// The requested project can compile alongside this task; callers must observe its completion.
/// </summary>
internal static class ColdCompilationPrimer
{
    internal const int MaxConcurrentRequests = 3;

    // Only primer roots enter this gate. Recursive Roslyn compilation never enters it, so a
    // dependency cannot deadlock waiting for a slot held by its parent. The bound is shared by
    // simultaneous editor requests, not multiplied by the number of completion requests.
    private static readonly SemaphoreSlim s_admission = new(MaxConcurrentRequests);

    public static Task PrimeAsync(Project project, CancellationToken ct) =>
        PrimeAsync(project, static (dependency, token) => dependency.GetCompilationAsync(token), ct);

    /// <summary>Per-call seam for testing admission without replacing any Roslyn service.</summary>
    internal static async Task PrimeAsync(
        Project project, Func<Project, CancellationToken, Task> compile, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!project.SupportsCompilation || project.TryGetCompilation(out _))
            return;

        // Never consult Workspace.CurrentSolution after capturing the request. Edits, project
        // loads and reconciles may advance it while these workers are waiting for admission.
        var solution = project.Solution;
        var graph = solution.GetProjectDependencyGraph();
        var closure = graph.GetProjectsThatThisProjectTransitivelyDependsOn(project.Id).ToHashSet();
        if (closure.Count == 0)
            return;

        await PrimeProjectsAsync(solution, graph.GetTopologicallySortedProjects(ct)
            .Where(closure.Contains), compile, ct).ConfigureAwait(false);
    }

    internal static Task PrimeSolutionAsync(
        Solution solution, IReadOnlySet<ProjectId> scope,
        Func<Project, CancellationToken, Task> compile, CancellationToken ct) =>
        PrimeProjectsAsync(solution, solution.GetProjectDependencyGraph().GetTopologicallySortedProjects(ct)
            .Where(scope.Contains), compile, ct);

    /// <summary>
    /// Primes only the caller's loaded search scope. Disposal requests cancellation without
    /// making the foreground wait for a generator that is slow to observe cancellation.
    /// The worker tasks and cancellation callbacks are observed before disposing their source.
    /// </summary>
    internal static PrimingSession Start(Solution solution, IReadOnlySet<ProjectId> scope, CancellationToken ct,
        Func<Project, CancellationToken, Task>? compile = null) => new(solution, scope,
            compile ?? (static (project, token) => project.GetCompilationAsync(token)), ct);

    internal sealed class PrimingSession : IDisposable
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly Task _work;
        private int _disposed;
        internal Task Stopped { get; private set; } = Task.CompletedTask;

        internal PrimingSession(Solution solution, IReadOnlySet<ProjectId> scope,
            Func<Project, CancellationToken, Task> compile, CancellationToken ct)
        {
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Capture synchronous setup exceptions in the task too, so an optional primer
            // cannot prevent the foreground operation or leak its cancellation source.
            _work = RunAsync();
            async Task RunAsync() => await PrimeSolutionAsync(solution, scope, compile, _cancellation.Token);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Stopped = StopAsync();
        }

        private async Task StopAsync()
        {
            try
            {
                await Task.WhenAll(_work, _cancellation.CancelAsync()).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
            catch (Exception ex)
            {
                ServiceLog.Warn($"Could not prime search compilations: {ex.Message}", key: "search-compilation-primer");
            }
            finally { _cancellation.Dispose(); }
        }
    }

    private static async Task PrimeProjectsAsync(Solution solution, IEnumerable<ProjectId> order,
        Func<Project, CancellationToken, Task> compile, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var dependencies = order
            .Select(id => solution.GetProject(id))
            .OfType<Project>()
            // Roslyn cannot form a project reference to a netmodule and skips it itself.
            .Where(p => p.SupportsCompilation && p.CompilationOptions?.OutputKind != OutputKind.NetModule
                && !p.TryGetCompilation(out _))
            .ToArray();
        if (dependencies.Length == 0)
            return;

        int next = -1;
        var workers = new Task[Math.Min(MaxConcurrentRequests, dependencies.Length)];
        for (int i = 0; i < workers.Length; i++)
        {
            // Compilation and file reads can run synchronously before their first await.
            // Three workers, rather than one task per project, also bound queued work.
            workers[i] = Task.Run(WorkAsync, CancellationToken.None);
        }
        await Task.WhenAll(workers).ConfigureAwait(false);

        async Task WorkAsync()
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int index = Interlocked.Increment(ref next);
                if (index >= dependencies.Length)
                    return;

                var dependency = dependencies[index];
                await s_admission.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    // Another worker or the foreground's recursive bind may already have
                    // completed this exact tracker while admission was pending.
                    if (dependency.TryGetCompilation(out _))
                        continue;

                    var timing = RunwayTrace.Begin("cold dependency");
                    timing?.Mark($"start {dependency.Name}");
                    await compile(dependency, ct).ConfigureAwait(false);
                    timing?.Mark("compiled");
                }
                finally
                {
                    s_admission.Release();
                }
            }
        }
    }
}

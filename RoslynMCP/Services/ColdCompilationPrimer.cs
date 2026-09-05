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

        var dependencies = graph.GetTopologicallySortedProjects(ct)
            .Where(closure.Contains)
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

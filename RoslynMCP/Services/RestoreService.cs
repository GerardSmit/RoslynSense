using System.Collections.Concurrent;
using System.Diagnostics;

namespace RoslynMCP.Services;

/// <summary>
/// Runs <c>dotnet restore</c> at most once per target, for the whole solution when there is one.
/// </summary>
/// <remarks>
/// <para>
/// <c>MSBuildWorkspace</c> never restores — Roslyn has no restore logic at all, and the
/// feature request for it (dotnet/roslyn#52293) is still open — so a project whose
/// <c>obj/project.assets.json</c> is missing evaluates with no NuGet graph and comes back with
/// unresolved references. Something has to run it, and the only question is what granularity and
/// how many times.
/// </para>
/// <para>
/// Per project, lazily, was the previous answer and it is the wrong one twice over. A restore is a
/// <c>dotnet</c> process start plus a NuGet graph walk — a second and a half on a warm package
/// cache, considerably more on a cold one — and a solution loading N projects paid it N times,
/// serialized, because each load waited for the one before it. On a generated 34-project solution
/// that measured 13.4 seconds of pure subprocess time to answer a question one restore already
/// answers: <c>dotnet restore &lt;sln&gt;</c> writes <c>project.assets.json</c> for <em>every</em>
/// project the solution lists, in a single graph walk, for about the cost of one.
/// </para>
/// <para>
/// So the target is the owning solution whenever the project has one, and the project itself
/// otherwise. Both are single-flighted through <see cref="s_inflight"/>: several projects of one
/// solution loading at once — which is the normal case, since that is exactly when this is
/// expensive — collapse onto one subprocess instead of racing each other to write the same files.
/// </para>
/// <para>
/// Deliberately not cached beyond the in-flight window. A completed restore leaves
/// <c>project.assets.json</c> on disk, and <see cref="NeedsRestore"/> reads that file rather than
/// any memory of having run: a memo would go on claiming the project was restored after a
/// <c>git clean</c>, an <c>obj/</c> wipe or a package downgrade, and the failure mode of a stale
/// "already restored" is a solution that will not resolve anything until the process is restarted.
/// </para>
/// </remarks>
internal static class RestoreService
{
    /// <summary>Restore target (solution or project path) → the run currently in flight for it.</summary>
    private static readonly ConcurrentDictionary<string, Task> s_inflight =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether restore evaluates the project graph once up front rather than walking it with the
    /// legacy recursive MSBuild task. On by default; set <c>ROSLYNMCP_NO_STATIC_GRAPH_RESTORE</c>
    /// to turn it off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Worth about 3.5x on a solution of any size — measured 5,885 ms → 1,645 ms on this
    /// repository's own solution — because the legacy walk re-evaluates a project once per path
    /// that reaches it, while the graph evaluates it once in total.
    /// </para>
    /// <para>
    /// It carries a real risk, and it is not the one the retry below covers. Static-graph
    /// evaluation builds the graph from <em>evaluation-time</em> <c>ProjectReference</c> items, so
    /// a project that adds references from inside a target is invisible to it — and that restore
    /// <em>succeeds</em>, writing a <c>project.assets.json</c> that is quietly missing entries. The
    /// symptom is unresolved references in a project that builds fine from the command line, which
    /// reads as a defect in this tool rather than in the restore that produced it. NuGet keeps the
    /// option opt-in for exactly this class of project.
    /// </para>
    /// <para>
    /// The blast radius is already narrow — legacy projects never reach here at all, since
    /// <see cref="NeedsRestore"/> skips anything without a <c>project.assets.json</c> to want, and
    /// <see cref="RestoreTargetFor"/> refuses a solution-level target for a solution containing one
    /// — so this applies to an all-SDK solution or a lone SDK project. The switch exists for the
    /// case that narrowing does not cover, so somebody hitting it can rule it out in one
    /// environment variable instead of a bisect.
    /// </para>
    /// </remarks>
    private static readonly bool s_useStaticGraph =
        Environment.GetEnvironmentVariable("ROSLYNMCP_NO_STATIC_GRAPH_RESTORE") is not ("1" or "true" or "on");

    /// <summary>
    /// Ensures <paramref name="projectPath"/> has a NuGet graph on disk, restoring its owning
    /// solution (or the project alone) if it does not. Returns immediately when the project is
    /// already restored or does not use <c>project.assets.json</c> at all.
    /// </summary>
    /// <remarks>
    /// Callers must invoke this <em>before</em> taking any workspace load gate. It starts a
    /// subprocess and waits on the network in the worst case, and holding a gate across that is
    /// what makes one project's cold restore everybody else's latency.
    /// </remarks>
    public static async Task EnsureRestoredAsync(string projectPath, CancellationToken cancellationToken)
    {
        if (!NeedsRestore(projectPath))
            return;

        string target = RestoreTargetFor(projectPath);
        var run = s_inflight.GetOrAdd(target, RunAsync);

        try
        {
            await run.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A failed restore is reported by RunAsync and then let go: the project loads with
            // whatever MSBuild can resolve, which is a degraded project rather than a failed
            // request, and is a far better outcome than refusing to open it at all.
        }
        finally
        {
            // Removed so the next caller retries rather than joining a completed run — a restore
            // that failed on a transient network blip should not poison the solution for the life
            // of the process. Removal is keyed on the task identity, so a concurrent caller that
            // has already started a *newer* run does not have it dropped out from under it.
            s_inflight.TryRemove(new KeyValuePair<string, Task>(target, run));
        }
    }

    /// <summary>
    /// Starts the restore a solution will need, without waiting for it, and does nothing at all
    /// when every project in it already has a NuGet graph on disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called when a solution is bound rather than when a project is first loaded, because those
    /// two moments are seconds apart and the restore does not care which one it runs in. The editor
    /// spends that gap parsing the <c>.sln</c>, drawing the tree and answering the first few
    /// structural requests — none of which need NuGet, and all of which used to finish and then
    /// wait. Measured on a 34-project solution that overlap is worth about 1.7 s off the first
    /// request that needs a real project.
    /// </para>
    /// <para>
    /// The guard is what keeps this from being a background job nobody asked for: on any repository
    /// that has been built or restored once, every <c>project.assets.json</c> exists, this returns
    /// without starting anything, and the cost is one <c>File.Exists</c> per project. It fires only
    /// on a genuinely un-restored solution — a fresh clone — where nothing the editor can offer
    /// works until a restore has happened anyway.
    /// </para>
    /// </remarks>
    public static void StartSolutionRestoreInBackground(string solutionPath)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var projects = PathHelper.GetProjectsFromSolution(solutionPath);
                string? unrestored = projects.FirstOrDefault(NeedsRestore);
                if (unrestored is null)
                    return;

                await EnsureRestoredAsync(unrestored, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Nothing awaits this, so an exception escaping it would be an unobserved task
                // exception and, depending on host configuration, the end of the process — for a
                // speculative warm-up whose failure the on-demand path handles by simply doing the
                // restore itself.
                Console.Error.WriteLine(
                    $"[Restore] Background restore of '{Path.GetFileName(solutionPath)}' failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Whether this project needs a restore at all: SDK-style, and no <c>project.assets.json</c>.
    /// </summary>
    /// <remarks>
    /// Legacy (non-SDK) projects use <c>packages.config</c> and never produce a
    /// <c>project.assets.json</c>, so testing for one would restore them on every single call.
    /// </remarks>
    private static bool NeedsRestore(string projectPath)
    {
        string? projectDir = Path.GetDirectoryName(projectPath);
        if (projectDir is null)
            return false;

        if (PathHelper.RequiresMsBuild(projectPath))
            return false;

        return !File.Exists(Path.Combine(projectDir, "obj", "project.assets.json"));
    }

    /// <summary>
    /// The owning solution when the project belongs to one, else the project itself.
    /// </summary>
    /// <remarks>
    /// A solution-wide restore covers every project the <c>.sln</c> lists, so the N-1 loads after
    /// the first find their assets already on disk and never reach <see cref="RunAsync"/>. The
    /// fallback matters for the cases <c>dotnet restore &lt;sln&gt;</c> cannot serve — a loose
    /// project with no solution above it, and a project whose solution the CLI refuses because a
    /// sibling in it is packages.config-era.
    /// </remarks>
    private static string RestoreTargetFor(string projectPath)
    {
        try
        {
            string? solution = PathHelper.FindNearestSolution(projectPath);
            if (solution is { Length: > 0 }
                && !PathHelper.IsLegacySolution(solution)
                && PathHelper.GetProjectsFromSolution(solution).Any(p =>
                    string.Equals(Path.GetFullPath(p), Path.GetFullPath(projectPath), StringComparison.OrdinalIgnoreCase)))
            {
                return Path.GetFullPath(solution);
            }
        }
        catch
        {
            // Fall through to the project — discovery failing is not a reason not to restore.
        }

        return Path.GetFullPath(projectPath);
    }

    /// <summary>
    /// The restore itself. Uncancellable by design: it is shared by every caller waiting on the
    /// same target, so the first one to give up must not take the others' restore down with it —
    /// <see cref="EnsureRestoredAsync"/> honours each caller's own token while it waits instead.
    /// </summary>
    private static async Task RunAsync(string target)
    {
        // Off the caller's stack: GetOrAdd runs its factory inline, so without this the first
        // caller would run the whole restore synchronously inside the dictionary's update.
        await Task.Yield();

        var watch = Stopwatch.StartNew();
        Console.Error.WriteLine($"[Restore] Restoring '{Path.GetFileName(target)}'...");

        var (exitCode, output) = await RunOnceAsync(target, staticGraph: s_useStaticGraph);
        if (exitCode == 0)
        {
            Console.Error.WriteLine(
                $"[Restore] '{Path.GetFileName(target)}' restored in {watch.ElapsedMilliseconds} ms.");
            return;
        }

        if (!s_useStaticGraph)
        {
            Console.Error.WriteLine(
                $"[Restore] 'dotnet restore \"{target}\"' failed (exit {exitCode}) after " +
                $"{watch.ElapsedMilliseconds} ms.\n{output}");
            return;
        }

        // A failure is not reported as a failure until the legacy path has also refused: the fast
        // path is an optimisation, and an optimisation that turns a restorable solution into an
        // unrestorable one is not one.
        Console.Error.WriteLine(
            $"[Restore] Static-graph restore of '{Path.GetFileName(target)}' failed (exit {exitCode}); " +
            "retrying with the default evaluation.");

        (exitCode, output) = await RunOnceAsync(target, staticGraph: false);
        if (exitCode == 0)
        {
            Console.Error.WriteLine(
                $"[Restore] '{Path.GetFileName(target)}' restored in {watch.ElapsedMilliseconds} ms " +
                "(static-graph evaluation did not apply).");
            return;
        }

        // Both streams, and the command: a restore failure is nearly always a feed, a credential or
        // a version conflict, and none of those are diagnosable from an exit code.
        Console.Error.WriteLine(
            $"[Restore] 'dotnet restore \"{target}\"' failed (exit {exitCode}) after " +
            $"{watch.ElapsedMilliseconds} ms.\n{output}");
    }

    /// <summary>
    /// One <c>dotnet restore</c>, returning its exit code and combined output rather than throwing.
    /// </summary>
    /// <param name="staticGraph">
    /// Whether to evaluate the project graph once up front instead of walking it with the legacy
    /// recursive MSBuild task. Measured on this repository's own solution: 5,885 ms → 1,645 ms.
    /// The win grows with the reference graph, because the legacy walk re-evaluates a project once
    /// per path that reaches it rather than once in total.
    /// </param>
    private static async Task<(int ExitCode, string Output)> RunOnceAsync(string target, bool staticGraph)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"restore \"{target}\" --verbosity quiet"
                    + (staticGraph ? " /p:RestoreUseStaticGraphEvaluation=true" : ""),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(target)!,
            }
        };

        // Also what keeps MSBuild from trying to reuse a worker node left behind by an earlier
        // build. Reconnecting to a dead one waits out MSBUILDNODECONNECTIONTIMEOUT — 900 seconds by
        // default — which turns a three-second restore into a fifteen-minute one with nothing in
        // the output to explain it.
        BuildProcessHelper.ConfigureMsBuildEnvironment(process.StartInfo);

        try
        {
            BuildProcessHelper.StartWithClosedInput(process);

            // Drained in parallel: a restore that fills either pipe while we wait on the other
            // deadlocks, and a NuGet graph walk produces enough output to do it.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);

            return (process.ExitCode, $"{(await stdout).Trim()}\n{(await stderr).Trim()}".Trim());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}

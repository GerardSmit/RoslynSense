using System.Collections.Concurrent;
using System.Diagnostics;
using RoslynMCP.Services.Packages;

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
/// <c>project.assets.json</c> on disk, and <see cref="DetermineNeed"/> reads that file rather than
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
    /// The blast radius is already narrow: this is a dotnet-CLI option, and anything non-SDK —
    /// which is every project whose restore <see cref="EngineFor"/> hands to the Visual Studio
    /// MSBuild — never sees it, so it applies to an all-SDK solution or a lone SDK project. The
    /// switch exists for the case that narrowing does not cover, so somebody hitting it can rule it
    /// out in one environment variable instead of a bisect.
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
        var need = DetermineNeed(projectPath);
        if (need == RestoreNeed.None)
            return;

        string target = RestoreTargetFor(projectPath, need);
        var run = s_inflight.GetOrAdd(target, key => RunAsync(key, need));

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
                string? unrestored = projects.FirstOrDefault(p => DetermineNeed(p) != RestoreNeed.None);
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

    /// <summary>What a project wants restored, if anything.</summary>
    internal enum RestoreNeed
    {
        /// <summary>Nothing to restore: already restored, or the project has no NuGet graph at all.</summary>
        None,

        /// <summary>A <c>PackageReference</c> restore, which writes <c>obj/project.assets.json</c>.</summary>
        Assets,

        /// <summary>A <c>packages.config</c> restore, which fills the <c>packages/</c> folder.</summary>
        PackagesConfig,
    }

    /// <summary>
    /// What <paramref name="projectPath"/> needs restored before it can resolve its references.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Legacy means packages.config" is what this used to assume, and it is how a legacy project
    /// with <c>PackageReference</c> items — the shape every .NET Framework solution modernised in
    /// the last several years has, DNN Platform among them — got skipped entirely. That project
    /// wants an <c>obj/project.assets.json</c> exactly like an SDK one, never got a restore to write
    /// it, and evaluated with no NuGet graph: every package-provided namespace came back as
    /// "The type or namespace name 'Extensions' does not exist in the namespace 'Microsoft'", in a
    /// solution that builds perfectly from the command line.
    /// </para>
    /// <para>
    /// So the question asked here is not "is this project SDK-style" but "which of the two restore
    /// shapes does this project file actually use", answered from the project's own items. A legacy
    /// project can want either, and a few want both — <see cref="RestoreNeed.PackagesConfig"/> wins
    /// there, because the MSBuild restore that serves it also restores the
    /// <c>PackageReference</c> half in the same pass.
    /// </para>
    /// </remarks>
    internal static RestoreNeed DetermineNeed(string projectPath)
    {
        string? projectDir = Path.GetDirectoryName(projectPath);
        if (projectDir is null)
            return RestoreNeed.None;

        // packages.config first: it is the one shape whose restore output is not a file in obj/, so
        // an existing project.assets.json says nothing about whether it has been restored.
        if (PackagesConfigService.Uses(projectPath) && PackagesConfigNeedsRestore(projectPath))
            return RestoreNeed.PackagesConfig;

        if (File.Exists(Path.Combine(projectDir, "obj", "project.assets.json")))
            return RestoreNeed.None;

        // An SDK project always has a NuGet graph, even with no PackageReference of its own: the
        // framework references come through it, so a missing assets file always means restore.
        if (!PathHelper.RequiresMsBuild(projectPath))
            return RestoreNeed.Assets;

        // A legacy project only has one if it actually declares PackageReference items. Read from
        // the project XML rather than from an evaluation, because this is asked before the project
        // has ever been loaded — that is the whole point of it.
        return UsesPackageReference(projectPath) ? RestoreNeed.Assets : RestoreNeed.None;
    }

    /// <summary>
    /// Whether the project file declares any <c>PackageReference</c> item, cached on the file's
    /// identity so repeated loads of one project do not re-read it.
    /// </summary>
    /// <remarks>
    /// A substring test rather than an XML parse, deliberately: the answer only has to be right
    /// about whether a restore is worth starting, a legacy project file can be several thousand
    /// lines (DNN's web project is 3,400), and a false positive costs one restore that writes an
    /// assets file nothing reads while a false negative costs every package reference in the
    /// project. <c>Directory.Packages.props</c> is not consulted for the same reason: central
    /// versions describe versions, not whether this project references anything.
    /// </remarks>
    private static bool UsesPackageReference(string projectPath) =>
        PathHelper.FileDerived<bool>.Get(projectPath, static path =>
        {
            try
            {
                return File.ReadAllText(path)
                    .Contains("<PackageReference", StringComparison.OrdinalIgnoreCase);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        });

    /// <summary>
    /// Whether any package the project's <c>packages.config</c> lists is missing from the packages
    /// folder. Checked per package folder because a partially-restored tree is the normal state
    /// after a package is added by hand.
    /// </summary>
    private static bool PackagesConfigNeedsRestore(string projectPath)
    {
        try
        {
            var entries = PackagesConfigService.Read(projectPath);
            if (entries.Count == 0)
                return false;

            string root = PackagesConfigService.PackagesRootFor(projectPath);
            return entries.Any(e => !Directory.Exists(Path.Combine(root, $"{e.Id}.{e.Version}")));
        }
        catch (Exception ex)
        {
            // A packages.config that cannot be read is not a reason to refuse to open the project;
            // it is a reason not to claim it needs restoring.
            Console.Error.WriteLine(
                $"[Restore] Could not read packages.config for '{Path.GetFileName(projectPath)}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The owning solution when the project belongs to one and the engine that will restore it can
    /// take a solution, else the project itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A solution-wide restore covers every project the <c>.sln</c> lists, so the N-1 loads after
    /// the first find their assets already on disk and never reach <see cref="RunAsync"/>. The
    /// fallback matters for the cases the engine cannot serve — a loose project with no solution
    /// above it, and a legacy solution when no Visual Studio MSBuild is installed to restore it.
    /// </para>
    /// <para>
    /// A legacy solution used to be refused outright here, on the grounds that
    /// <c>dotnet restore &lt;sln&gt;</c> chokes on a non-SDK project in it. That is true of the CLI
    /// and not of the engine now chosen for those solutions: <c>MSBuild.exe -t:Restore</c> restores
    /// a mixed solution in one graph walk, which is both correct and the cheap way to serve the
    /// twenty-odd projects a legacy web solution pulls into one closure.
    /// </para>
    /// </remarks>
    private static string RestoreTargetFor(string projectPath, RestoreNeed need)
    {
        try
        {
            string? solution = PathHelper.FindNearestSolution(projectPath);
            if (solution is { Length: > 0 }
                && PathHelper.GetProjectsFromSolution(solution).Any(p =>
                    string.Equals(Path.GetFullPath(p), Path.GetFullPath(projectPath), StringComparison.OrdinalIgnoreCase))
                && EngineFor(solution, need) is not null)
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
    /// Which restore engine can serve <paramref name="target"/>, or <c>null</c> when none can.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The .NET CLI is used wherever it works, because it is on every machine that runs this and it
    /// carries the static-graph optimisation. It does not work on a non-SDK project: the SDK's
    /// MSBuild cannot resolve <c>$(MSBuildExtensionsPath)\...\Microsoft.WebApplication.targets</c>
    /// and the like, so a legacy web project comes back as an evaluation failure rather than as a
    /// restore.
    /// </para>
    /// <para>
    /// Those go to the Visual Studio MSBuild already located for the legacy <c>BuildHost</c>, which
    /// is the same engine Visual Studio itself restores them with. When there is none, there is no
    /// engine — the project could not have been loaded in the first place on that machine.
    /// </para>
    /// </remarks>
    private static RestoreEngine? EngineFor(string target, RestoreNeed need)
    {
        bool legacy = need == RestoreNeed.PackagesConfig || PathHelper.RequiresMsBuild(target);
        if (!legacy)
            return RestoreEngine.DotnetCli;

        return WorkspaceService.LegacyMsBuildDirectory is { Length: > 0 }
            ? RestoreEngine.VisualStudioMsBuild
            : null;
    }

    /// <summary>The process that runs a restore.</summary>
    private enum RestoreEngine
    {
        /// <summary><c>dotnet restore</c>, for SDK-style targets.</summary>
        DotnetCli,

        /// <summary><c>MSBuild.exe -t:Restore</c>, for anything non-SDK.</summary>
        VisualStudioMsBuild,
    }

    /// <summary>
    /// The restore itself. Uncancellable by design: it is shared by every caller waiting on the
    /// same target, so the first one to give up must not take the others' restore down with it —
    /// <see cref="EnsureRestoredAsync"/> honours each caller's own token while it waits instead.
    /// </summary>
    private static async Task RunAsync(string target, RestoreNeed need)
    {
        // Off the caller's stack: GetOrAdd runs its factory inline, so without this the first
        // caller would run the whole restore synchronously inside the dictionary's update.
        await Task.Yield();

        if (EngineFor(target, need) is not { } engine)
        {
            // Said once, with what to do about it: on a machine with no Visual Studio MSBuild a
            // legacy project cannot be restored at all, and every package reference in it reads as
            // a missing assembly. Silence here is how that becomes a bug report about this tool
            // rather than about the machine.
            Console.Error.WriteLine(
                $"[Restore] '{Path.GetFileName(target)}' needs a Visual Studio MSBuild to restore " +
                "(non-SDK project) and none is installed; its package references will not resolve. " +
                "Install 'Visual Studio Build Tools' (2017 or later) with the MSBuild component.");
            return;
        }

        var watch = Stopwatch.StartNew();
        Console.Error.WriteLine(
            $"[Restore] Restoring '{Path.GetFileName(target)}'" +
            $"{(engine == RestoreEngine.VisualStudioMsBuild ? " with MSBuild" : "")}...");

        // Static-graph evaluation is a dotnet-CLI restore option, and is left off for the MSBuild
        // engine, whose targets are exactly the ones NuGet keeps it opt-in for.
        bool staticGraph = engine == RestoreEngine.DotnetCli && s_useStaticGraph;

        var (exitCode, output) = await RunOnceAsync(target, staticGraph, engine, need);
        if (exitCode == 0)
        {
            Console.Error.WriteLine(
                $"[Restore] '{Path.GetFileName(target)}' restored in {watch.ElapsedMilliseconds} ms.");
            RefreshRestoredProjects(target);
            return;
        }

        if (!staticGraph)
        {
            Console.Error.WriteLine(
                $"[Restore] Restore of '{Path.GetFileName(target)}' failed (exit {exitCode}) after " +
                $"{watch.ElapsedMilliseconds} ms.\n{output}");
            return;
        }

        // A failure is not reported as a failure until the legacy path has also refused: the fast
        // path is an optimisation, and an optimisation that turns a restorable solution into an
        // unrestorable one is not one.
        Console.Error.WriteLine(
            $"[Restore] Static-graph restore of '{Path.GetFileName(target)}' failed (exit {exitCode}); " +
            "retrying with the default evaluation.");

        (exitCode, output) = await RunOnceAsync(target, staticGraph: false, engine, need);
        if (exitCode == 0)
        {
            Console.Error.WriteLine(
                $"[Restore] '{Path.GetFileName(target)}' restored in {watch.ElapsedMilliseconds} ms " +
                "(static-graph evaluation did not apply).");
            RefreshRestoredProjects(target);
            return;
        }

        // Both streams, and the command: a restore failure is nearly always a feed, a credential or
        // a version conflict, and none of those are diagnosable from an exit code.
        Console.Error.WriteLine(
            $"[Restore] Restore of '{Path.GetFileName(target)}' failed (exit {exitCode}) after " +
            $"{watch.ElapsedMilliseconds} ms.\n{output}");
    }

    /// <summary>
    /// Drops any cached workspace built before this restore, so the next request reloads the
    /// projects now that they have a NuGet graph to evaluate against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ordinary path does not need this: <see cref="EnsureRestoredAsync"/> is awaited before the
    /// load, so the first evaluation already sees the assets file. The restores that do need it are
    /// the ones nothing was waiting on — the background solution restore started at bind time, and
    /// the case that produced it: a project already loaded <em>without</em> a restore, because the
    /// legacy skip meant it never got one. Those workspaces hold a compilation whose package
    /// references are unresolved, and nothing else in the process will ever correct them — no file
    /// the editor watches changed, and the projects' own timestamps did not move.
    /// </para>
    /// <para>
    /// Eviction rather than a targeted reference fix-up, because the difference between the two
    /// evaluations is not only the metadata references: the analysers, source generators and
    /// framework closure a restore provides all come out of the same graph.
    /// </para>
    /// </remarks>
    private static void RefreshRestoredProjects(string target)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var projects = PathHelper.IsSolutionFile(target)
                    ? PathHelper.GetProjectsFromSolution(target)
                    : (IReadOnlyList<string>)[target];

                int evicted = 0;
                foreach (string project in projects)
                {
                    if (await WorkspaceService.EvictProjectIfLoadedAsync(project))
                        evicted++;
                }

                if (evicted > 0)
                {
                    Console.Error.WriteLine(
                        $"[Restore] Evicted {evicted} workspace(s) loaded before the restore of " +
                        $"'{Path.GetFileName(target)}'; they reload with their NuGet graph.");
                }
            }
            catch (Exception ex)
            {
                // Nothing awaits this. A refresh that fails leaves the workspace exactly as stale as
                // it was before, which is the behaviour this replaced.
                Console.Error.WriteLine($"[Restore] Post-restore refresh failed: {ex.Message}");
            }
        });
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
    private static async Task<(int ExitCode, string Output)> RunOnceAsync(
        string target, bool staticGraph, RestoreEngine engine, RestoreNeed need)
    {
        var (fileName, arguments) = engine switch
        {
            // -nr:false so no worker node is left behind holding the project files: a lingering node
            // keeps handles on the tree, and the next git operation over it fails on them.
            // RestorePackagesConfig is what makes the packages/ folder half happen at all, and is
            // inert on a project that has no packages.config.
            RestoreEngine.VisualStudioMsBuild => (
                Path.Combine(WorkspaceService.LegacyMsBuildDirectory!, "MSBuild.exe"),
                $"\"{target}\" -t:Restore -v:quiet -nologo -nr:false"
                    + (need == RestoreNeed.PackagesConfig ? " -p:RestorePackagesConfig=true" : "")),

            _ => (
                "dotnet",
                $"restore \"{target}\" --verbosity quiet"
                    + (staticGraph ? " /p:RestoreUseStaticGraphEvaluation=true" : "")),
        };

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
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

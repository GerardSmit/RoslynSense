using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynMCP.Services;

/// <summary>
/// One long-lived MSBuild BuildHost per set of global properties, shared by every project load.
/// </summary>
/// <remarks>
/// <para>
/// Roslyn's <c>MSBuildProjectLoader</c> creates a <c>BuildHostProcessManager</c> inside every
/// top-level <c>OpenProjectAsync</c> / <c>OpenSolutionAsync</c> and disposes it when the call
/// returns. The subprocess is cheap — measured at 75 ms — but what dies with it is not: MSBuild's
/// assemblies are loaded and JIT-ed, its toolsets and SDK resolvers are discovered, and the SDK's
/// several hundred <c>.props</c> and <c>.targets</c> are parsed, all from cold, and all again for
/// the next call.
/// </para>
/// <para>
/// Measured on six generated projects: a fresh host per project costs 1,704 ms each in steady
/// state, and one reused host costs 230 ms each — 7.4x — with the first load through it paying the
/// initialisation once (1,224 ms) on everyone's behalf. That is the difference between a load being
/// dominated by MSBuild starting up and being dominated by the project it was asked about.
/// </para>
/// <para>
/// Keyed by global properties because they are baked into the host's <c>ProjectCollection</c>: two
/// solutions that disagree about <c>SolutionDir</c>, or a legacy solution that needs
/// <c>DesignTimeBuild</c> without <c>AlwaysUseNETSdkDefaults</c>, cannot share one. In practice
/// that is one host for the ordinary case and one more per solution that supplies its own
/// properties.
/// </para>
/// </remarks>
internal static class SharedBuildHost
{
    private static readonly ConcurrentDictionary<string, Lazy<BuildHostProcessManager>> s_managers =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Serializes loads through one manager. <c>BuildHostProcessManager</c> guards its own process
    /// table, but the build inside the host is a single MSBuild <c>BuildManager</c> with one
    /// in-process node — asking it to evaluate two projects at once is not a supported shape, and
    /// the win here comes from the host being warm rather than from overlapping work inside it.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_gates =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Loads <paramref name="projectPaths"/> through the shared host for
    /// <paramref name="properties"/>, returning the project models Roslyn built for them.
    /// </summary>
    /// <param name="projectMapFactory">
    /// Builds the identity of what the target workspace already holds. Roslyn resolves a
    /// <c>ProjectReference</c> against it, so a project already loaded keeps its
    /// <see cref="ProjectId"/> and the returned models point at it rather than at a duplicate.
    /// <para>
    /// A factory rather than an instance, and that is not a style choice. <c>ProjectMap</c> holds
    /// plain unsynchronised dictionaries, so handing one to shards that run concurrently is a data
    /// race — and it does not fail loudly. It corrupts, and the load comes back with fewer projects
    /// than were asked for or none at all, which the caller can only read as "the batch did not
    /// work" before falling back to loading them one at a time. Each shard gets its own.
    /// </para>
    /// </param>
    public static async Task<ImmutableArray<ProjectInfo>> LoadAsync(
        Workspace workspace,
        ImmutableDictionary<string, string> properties,
        IReadOnlyList<string> projectPaths,
        Func<ProjectMap> projectMapFactory,
        CancellationToken cancellationToken)
    {
        // Sharded across several warm hosts, because the two costs are independent. Keeping a host
        // alive removes MSBuild's start-up; running several removes the sequencing. One host does
        // not give both: inside it there is a single MSBuild BuildManager with one in-process node,
        // and BeginBuild/EndBuild is not re-entrant, so concurrency has to come from more hosts.
        int shardCount = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        shardCount = Math.Min(shardCount, projectPaths.Count);

        var shards = new List<string>[shardCount];
        for (int i = 0; i < shardCount; i++)
            shards[i] = [];

        // Round-robin: adjacent entries in the wanted list are typically siblings referencing the
        // same things, so dealing them out spreads the transitive closures rather than
        // concentrating them in one shard.
        for (int i = 0; i < projectPaths.Count; i++)
            shards[i % shardCount].Add(projectPaths[i]);

        var results = await Task.WhenAll(shards.Select((shard, index) =>
            LoadShardAsync(workspace, properties, shard, index, projectMapFactory, cancellationToken)));

        return Reconcile(results);
    }

    private static async Task<ImmutableArray<ProjectInfo>> LoadShardAsync(
        Workspace workspace,
        ImmutableDictionary<string, string> properties,
        List<string> shard,
        int shardIndex,
        Func<ProjectMap> projectMapFactory,
        CancellationToken cancellationToken)
    {
        if (shard.Count == 0)
            return [];

        // Each shard gets its own host, keyed so the pool entry is stable across calls and stays
        // warm for the next batch rather than being rebuilt per request.
        string key = $"{shardIndex} {KeyFor(properties)}";
        var gate = s_gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken);
        try
        {
            // Before anything else: a host that has already evaluated one of these would answer
            // from its cache rather than from disk. See RecycleIfAlreadyEvaluatedAsync.
            await RecycleIfAlreadyEvaluatedAsync(key, shard);

            var manager = ManagerFor(key, properties);

            // A loader per call, which is cheap — it holds a diagnostic reporter and a file-extension
            // registry, not a process. The manager is what has to survive, and it does.
            var loader = new MSBuildProjectLoader(workspace, properties);
            var provider = new BuildHostProjectFileInfoProvider(
                manager, loader.ProjectFileExtensionRegistry, loader.Reporter, progress: null);

            // Built inside the shard, so no two concurrent loads ever touch the same map.
            var infos = await loader.LoadInfosAsync(
                [.. shard], provider, projectMapFactory(), progress: null, cancellationToken);

            // Recorded after the fact: every project this host has now evaluated, including the
            // transitive ones it pulled in, because those are cached in its ProjectCollection just
            // as firmly as the ones that were asked for.
            var seen = s_evaluated.GetOrAdd(key, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            lock (seen)
            {
                foreach (string path in shard)
                    seen.Add(Path.GetFullPath(path));

                foreach (var info in infos)
                {
                    if (info.FilePath is { Length: > 0 } p)
                        seen.Add(Path.GetFullPath(p));
                }
            }

            return infos;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Project paths each host has already evaluated, and therefore has cached.</summary>
    private static readonly ConcurrentDictionary<string, HashSet<string>> s_evaluated =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Replaces the host for <paramref name="key"/> when it has already evaluated one of
    /// <paramref name="shard"/>, because it would answer from cache rather than from disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the correctness price of keeping a host warm, and it is not optional.
    /// <c>BuildHost</c> builds its <c>ProjectBuildManager</c> once and leaves a batch build open for
    /// the life of the process, so <c>ProjectBuildManager.LoadProjectAsync</c> hits
    /// <c>projectCollection.GetLoadedProjects(path)</c> and hands back the <em>first</em> evaluation
    /// of that project for as long as the host lives. Roslyn never sees this because it disposes the
    /// whole host per top-level call.
    /// </para>
    /// <para>
    /// So a second load of the same project through the same host returns its old document list, its
    /// old references and its old options — a file added on disk is invisible, an edited
    /// <c>.csproj</c> has no effect, and nothing anywhere reports a problem. Measured as seven tests
    /// failing together on completion, rename, formatting and file-watching, all of which load a
    /// fixture project more than once.
    /// </para>
    /// <para>
    /// Recycling costs that host its warmth and nothing else. The case the pool exists for — loading
    /// many distinct projects of a solution — never repeats a path and never recycles; the case that
    /// does repeat is a reload, which has to re-read from disk to be correct anyway.
    /// </para>
    /// </remarks>
    private static async Task RecycleIfAlreadyEvaluatedAsync(string key, List<string> shard)
    {
        if (!s_evaluated.TryGetValue(key, out var seen))
            return;

        bool repeats;
        lock (seen)
            repeats = shard.Any(p => seen.Contains(Path.GetFullPath(p)));

        if (!repeats)
            return;

        if (s_managers.TryRemove(key, out var stale) && stale.IsValueCreated)
        {
            try
            {
                await stale.Value.DisposeAsync();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[BuildHost] Could not retire a stale host: {ex.Message}");
            }
        }

        lock (seen)
            seen.Clear();
    }

    private static BuildHostProcessManager ManagerFor(string key, ImmutableDictionary<string, string> properties) =>
        s_managers.GetOrAdd(key, _ => new Lazy<BuildHostProcessManager>(
            () => new BuildHostProcessManager(
                knownCommandLineParserLanguages: [LanguageNames.CSharp, LanguageNames.VisualBasic],
                globalMSBuildProperties: properties),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    /// <summary>
    /// Merges the shards into one set with one model per project file, rewriting references that
    /// pointed at a dropped duplicate.
    /// </summary>
    /// <remarks>
    /// Two shards can both pull in the same project — not one of the requested ones, but a shared
    /// dependency of two of them that the workspace does not hold yet. Each shard resolves its
    /// <see cref="ProjectMap"/> independently, so they mint different <see cref="ProjectId"/>s for
    /// the same file. Keeping both would put the same project in the solution twice and split its
    /// symbols; dropping one without rewriting would leave the other shard's reference dangling,
    /// which Roslyn renders as a project that cannot see types it plainly references.
    /// </remarks>
    private static ImmutableArray<ProjectInfo> Reconcile(ImmutableArray<ProjectInfo>[] shards)
    {
        var winnerByPath = new Dictionary<string, ProjectId>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<ProjectInfo>();

        foreach (var info in shards.SelectMany(s => s.IsDefault ? [] : s))
        {
            if (info.FilePath is not { Length: > 0 } path)
            {
                kept.Add(info);
                continue;
            }

            string full = Path.GetFullPath(path);
            if (winnerByPath.TryAdd(full, info.Id))
                kept.Add(info);
        }

        // Every id that lost, mapped to the one that won, so a reference to either resolves.
        var replacement = new Dictionary<ProjectId, ProjectId>();
        foreach (var info in shards.SelectMany(s => s.IsDefault ? [] : s))
        {
            if (info.FilePath is { Length: > 0 } path
                && winnerByPath.TryGetValue(Path.GetFullPath(path), out var winner)
                && winner != info.Id)
            {
                replacement[info.Id] = winner;
            }
        }

        if (replacement.Count == 0)
            return [.. kept];

        return
        [
            .. kept.Select(info => info.WithProjectReferences(
                info.ProjectReferences
                    .Select(r => replacement.TryGetValue(r.ProjectId, out var to)
                        ? new ProjectReference(to, r.Aliases, r.EmbedInteropTypes)
                        : r)
                    .DistinctBy(r => r.ProjectId)))
        ];
    }

    /// <summary>
    /// Starts the host for <paramref name="properties"/> and pays its MSBuild initialisation now,
    /// so the first project load does not.
    /// </summary>
    /// <remarks>
    /// Loads no project and reads no solution: it asks the manager for a host, which spawns the
    /// subprocess and lets it initialise. On a solution nobody goes on to open, the cost is one
    /// idle <c>dotnet</c> process — which is why this is called when a solution is explicitly
    /// bound, not on every path that happens to touch this class.
    /// </remarks>
    public static void WarmInBackground(ImmutableDictionary<string, string> properties, string anyProjectPath)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var watch = Stopwatch.StartNew();
                int shardCount = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

                // All of them, together: a batch fans out across the pool, so warming only the first
                // would leave the rest to initialise from cold inside the request that needs them.
                await Task.WhenAll(Enumerable.Range(0, shardCount).Select(async index =>
                {
                    string key = $"{index} {KeyFor(properties)}";
                    var manager = s_managers.GetOrAdd(key, _ => new Lazy<BuildHostProcessManager>(
                        () => new BuildHostProcessManager(
                            knownCommandLineParserLanguages: [LanguageNames.CSharp, LanguageNames.VisualBasic],
                            globalMSBuildProperties: properties),
                        LazyThreadSafetyMode.ExecutionAndPublication)).Value;

                    await manager.GetBuildHostWithFallbackAsync(anyProjectPath, CancellationToken.None);
                }));

                Console.Error.WriteLine(
                    $"[BuildHost] Warmed {shardCount} host(s) in {watch.ElapsedMilliseconds} ms; " +
                    "project loads now skip MSBuild's start-up.");
            }
            catch (Exception ex)
            {
                // Nothing awaits this, and the on-demand path creates its own host if this failed.
                Console.Error.WriteLine($"[BuildHost] Warm-up failed: {ex.Message}");
            }
        });
    }

    /// <summary>Tears down every host. Called when the process is shutting down.</summary>
    public static async Task DisposeAllAsync()
    {
        foreach (var (key, manager) in s_managers.ToArray())
        {
            s_managers.TryRemove(key, out _);
            if (!manager.IsValueCreated)
                continue;

            try
            {
                await manager.Value.DisposeAsync();
            }
            catch (Exception ex)
            {
                // A host that will not shut down cleanly must not stop the rest from being asked to:
                // each one is a subprocess, and leaving them behind is how a machine accumulates
                // orphaned MSBuild hosts.
                Console.Error.WriteLine($"[BuildHost] Shutdown of a build host failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// A stable identity for a global-property set. Ordered, because two dictionaries with the same
    /// contents in a different order describe the same MSBuild environment and must share a host.
    /// </summary>
    private static string KeyFor(ImmutableDictionary<string, string> properties) =>
        string.Join(" ", properties.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{p.Key}={p.Value}"));
}

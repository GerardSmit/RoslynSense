using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.RegularExpressions;
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
internal static partial class SharedBuildHost
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
        CancellationToken cancellationToken,
        ConcurrentDictionary<string, Lazy<Task<ImmutableArray<ProjectFileInfo>>>>? sharedEvaluations = null)
    {
        if (projectPaths.Count == 0)
            return [];

        var shards = new List<string>[PoolSize];
        for (int i = 0; i < PoolSize; i++)
            shards[i] = [];

        // One project is the interactive case — the seed load, an incremental add — and it gets to
        // choose its host rather than hash to one. Two things follow from choosing.
        //
        // It prefers low-numbered shards, because that is the order background warming works
        // through the pool, so the load the editor is waiting on meets the host that is warm (or is
        // being warmed, and is worth queueing behind) rather than a cold one three slots along.
        //
        // And it skips hosts that have already evaluated this project, which is a reroute in place
        // of a recycle. A warm host holding a stale evaluation used to be retired so the next load
        // read from disk; now the load simply goes to a host that never saw the project, which is
        // just as correct and costs nothing. Recycling is left for when every host has it.
        // One map for every shard of this call: the shard closures overlap heavily, and this is
        // what stops them evaluating each other's dependencies over again. A caller that already
        // has evaluations running — a solution open whose prewarm is still in flight — passes its
        // own map, and each shard here consumes those results the moment they complete. Scoped to
        // the call or to one open — never static — because a reload exists to re-read disk.
        var inFlight = sharedEvaluations ?? NewEvaluationMap();

        if (projectPaths.Count == 1)
        {
            int chosen = ChooseShardFor(projectPaths[0], properties);

            // An interactive load must not queue behind a whole-solution evaluation. When the
            // chosen shard's gate is held — a prewarm or a batch is running — the load takes a
            // host of its own instead of waiting minutes for someone else's work. The solo host
            // stays warm and keyed like any other, so the next contended single load reuses it.
            if (s_gates.TryGetValue($"{chosen} {KeyFor(properties)}", out var chosenGate)
                && chosenGate.CurrentCount == 0)
            {
                return Reconcile(
                    [await LoadShardAsync(
                        workspace, properties, [projectPaths[0]], SoloShard,
                        projectMapFactory, inFlight, cancellationToken)]);
            }

            shards[chosen].Add(projectPaths[0]);

            var single = await Task.WhenAll(shards.Select((shard, index) =>
                LoadShardAsync(workspace, properties, shard, index, projectMapFactory, inFlight, cancellationToken)));

            return Reconcile(single);
        }

        // Round-robin for balance, but starting from a shard derived from the first path rather than
        // always from zero. Both halves matter. Round-robin keeps a batch spread evenly, because
        // adjacent entries in the wanted list are typically siblings pulling in the same
        // dependencies. The offset is what stops shard 0 from serving every single-project load in
        // the process: it took every seed and every incremental add, so its set of already-evaluated
        // projects grew until nearly any reload of anything recycled it — while shards 1..3 sat
        // warm and idle. With the offset, a given project deterministically starts at the same
        // shard every time, so a genuine reload retires exactly the one host that had it cached.
        int start = ShardFor(projectPaths[0]);
        for (int i = 0; i < projectPaths.Count; i++)
            shards[(start + i) % PoolSize].Add(projectPaths[i]);

        var results = await Task.WhenAll(shards.Select((shard, index) =>
            LoadShardAsync(workspace, properties, shard, index, projectMapFactory, inFlight, cancellationToken)));

        return Reconcile(results);
    }

    /// <summary>Sentinel shard index for a host outside the round-robin pool.</summary>
    private const int SoloShard = -1;

    /// <summary>
    /// An empty evaluation map, for a caller that wants a prewarm and a load to share their
    /// in-flight evaluations. See <see cref="PrewarmEvaluationsAsync"/>.
    /// </summary>
    public static ConcurrentDictionary<string, Lazy<Task<ImmutableArray<ProjectFileInfo>>>> NewEvaluationMap() =>
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Evaluates <paramref name="projectPaths"/> across the pool and stores the results in the
    /// evaluation cache, producing no project models at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is how a solution open stops being sequential. The open's shape is seed first, batch
    /// second, and the seed's own evaluation used to stand in front of everything else the pool
    /// could have been doing. Started before the seed, this pass runs the whole solution's MSBuild
    /// evaluation — the actual wall-clock cost of a cold open — concurrently with it; by the time
    /// the batch runs, every project is a cache hit and the batch is conversion work only.
    /// </para>
    /// <para>
    /// It deliberately skips <c>MSBuildProjectLoader.LoadInfosAsync</c> and drives the providers
    /// directly: the loader's output is <c>ProjectInfo</c>s bound to a <c>ProjectMap</c> of some
    /// particular workspace, which is exactly the part that cannot be decided yet while the seed
    /// is still creating that workspace. Evaluation has no such binding, so only evaluation runs
    /// here. <paramref name="workspace"/> is a scratch workspace the caller owns, used for the
    /// loader services a provider needs; it must outlive this call and gains no projects.
    /// </para>
    /// </remarks>
    public static async Task PrewarmEvaluationsAsync(
        Workspace workspace,
        ImmutableDictionary<string, string> properties,
        IReadOnlyList<string> projectPaths,
        ConcurrentDictionary<string, Lazy<Task<ImmutableArray<ProjectFileInfo>>>> evaluations,
        CancellationToken cancellationToken)
    {
        if (projectPaths.Count == 0)
            return;

        var shards = new List<string>[PoolSize];
        for (int i = 0; i < PoolSize; i++)
            shards[i] = [];

        int start = ShardFor(projectPaths[0]);
        for (int i = 0; i < projectPaths.Count; i++)
            shards[(start + i) % PoolSize].Add(projectPaths[i]);

        await Task.WhenAll(shards.Select((shard, index) =>
            PrewarmShardAsync(workspace, properties, shard, index, evaluations, cancellationToken)));
    }

    private static async Task PrewarmShardAsync(
        Workspace workspace,
        ImmutableDictionary<string, string> properties,
        List<string> shard,
        int shardIndex,
        ConcurrentDictionary<string, Lazy<Task<ImmutableArray<ProjectFileInfo>>>> inFlight,
        CancellationToken cancellationToken)
    {
        if (shard.Count == 0)
            return;

        string key = $"{shardIndex} {KeyFor(properties)}";
        var gate = s_gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        var watch = Stopwatch.StartNew();

        Interlocked.Increment(ref s_inFlightLoads);
        try
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var loader = new MSBuildProjectLoader(workspace, properties);
                var provider = new CachingProjectFileInfoProvider(properties,
                    new Lazy<IProjectFileInfoProvider>(() => new BuildHostProjectFileInfoProvider(
                        ManagerFor(key, properties), loader.ProjectFileExtensionRegistry, loader.Reporter,
                        progress: null)),
                    inFlight);

                var misses = provider.Probe(shard);
                if (misses.Count == 0)
                    return;

                await RecycleIfAlreadyEvaluatedAsync(key, misses);

                var reporting = new DiagnosticReportingOptions(
                    DiagnosticReportingMode.Log, DiagnosticReportingMode.Log);

                // A queue rather than a foreach: each evaluation names its ProjectReferences, and
                // a reference outside the solution's own list — a legacy project only reachable
                // through another project — would otherwise be met for the first time by the
                // batch, on its critical path. The in-flight map keeps a reference two shards
                // discover from being evaluated twice.
                var pending = new Queue<string>(misses);
                var chased = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (pending.Count > 0)
                {
                    string path = pending.Dequeue();
                    string fullPath = Path.GetFullPath(path);
                    if (!chased.Add(fullPath))
                        continue;

                    // Claimed by another shard since it was enqueued: awaiting it would idle this
                    // host on someone else's work — the stall the prewarm exists to avoid. Only a
                    // path nobody owns is this shard's business.
                    if (inFlight.ContainsKey(fullPath))
                        continue;

                    try
                    {
                        var infos = await provider.LoadProjectFileInfosAsync(path, reporting, cancellationToken);

                        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
                        foreach (var info in infos)
                        {
                            if (info.ProjectReferences.IsDefault)
                                continue;

                            foreach (var reference in info.ProjectReferences)
                            {
                                string referencePath = Path.GetFullPath(Path.Combine(dir, reference.Path));
                                if (!chased.Contains(referencePath)
                                    && !inFlight.ContainsKey(referencePath)
                                    && File.Exists(referencePath))
                                {
                                    pending.Enqueue(referencePath);
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // The load that follows will evaluate this one itself and report properly;
                        // said out loud because every one of these is a project the prewarm failed
                        // to take off the batch's critical path.
                        Console.Error.WriteLine(
                            $"[BuildHost] Prewarm could not evaluate '{Path.GetFileName(path)}': {ex.Message}");
                    }
                }

                Console.Error.WriteLine(
                    $"[BuildHost] Prewarm shard {shardIndex}: {chased.Count} evaluation(s) in "
                    + $"{watch.ElapsedMilliseconds} ms.");

                // Same bookkeeping as a real load: these evaluations live in the host's
                // ProjectCollection now, and a future reload has to know to retire it.
                lock (provider.HostEvaluated)
                {
                    if (provider.HostEvaluated.Count > 0)
                    {
                        var seen = s_evaluated.GetOrAdd(key,
                            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                        lock (seen)
                        {
                            foreach (string path in provider.HostEvaluated)
                                seen.Add(path);
                        }
                    }
                }
            }
            finally
            {
                gate.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref s_inFlightLoads);
        }
    }

    /// <summary>
    /// How many warm hosts to keep. Each is a <c>dotnet</c> process holding a parsed copy of the
    /// SDK, so this trades memory for the ability to evaluate projects concurrently.
    /// <c>ROSLYNMCP_BUILDHOST_POOL</c> overrides the computed size (bounded 1–16), for machines
    /// where the computed size does not fit.
    /// </summary>
    /// <remarks>
    /// The cap is 6 because that is where an 80-project cold open measured fastest, and the curve
    /// is not subtle: on a 32-core machine, per-shard evaluation of the same solution took ~10.5s
    /// with 6 hosts, ~13s with 8, and 16 hosts were three times slower than 4 — MSBuild
    /// evaluation contends on something machine-wide (the evaluations per second barely move from
    /// 4 hosts up), so extra hosts past 6 only add spawn cost and contention.
    /// </remarks>
    private static readonly int PoolSize =
        int.TryParse(Environment.GetEnvironmentVariable("ROSLYNMCP_BUILDHOST_POOL"), out int configured)
            ? Math.Clamp(configured, 1, 16)
            : Math.Clamp(Environment.ProcessorCount / 2, 1, 6);

    /// <summary>
    /// The shard a project starts at — stable for a given path within the process, so the same
    /// project always meets the same host.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="string.GetHashCode()"/>: that is randomised per process, which
    /// would still be stable enough here, but a hash written down is one that can be reasoned about
    /// when a shard turns out to be hot.
    /// </remarks>
    /// <summary>
    /// Picks the host for a single-project load: the lowest-numbered one that has not already
    /// evaluated it, falling back to <see cref="ShardFor"/> when every host has.
    /// </summary>
    private static int ChooseShardFor(string projectPath, ImmutableDictionary<string, string> properties)
    {
        string full = Path.GetFullPath(projectPath);
        string suffix = KeyFor(properties);

        for (int index = 0; index < PoolSize; index++)
        {
            if (!s_evaluated.TryGetValue($"{index} {suffix}", out var seen))
                return index;   // Never used at all, so it cannot be holding a stale evaluation.

            lock (seen)
            {
                if (!seen.Contains(full))
                    return index;
            }
        }

        // Every host has this project cached. Whichever one is picked will be recycled, so pick the
        // stable one: a project that has to be reloaded repeatedly then keeps retiring the same
        // host instead of working its way around the pool retiring all of them.
        return ShardFor(projectPath);
    }

    private static int ShardFor(string projectPath)
    {
        uint hash = 2166136261;     // FNV-1a
        foreach (char c in Path.GetFullPath(projectPath))
        {
            hash ^= char.ToLowerInvariant(c);
            hash *= 16777619;
        }

        return (int)(hash % (uint)PoolSize);
    }

    private static async Task<ImmutableArray<ProjectInfo>> LoadShardAsync(
        Workspace workspace,
        ImmutableDictionary<string, string> properties,
        List<string> shard,
        int shardIndex,
        Func<ProjectMap> projectMapFactory,
        ConcurrentDictionary<string, Lazy<Task<ImmutableArray<ProjectFileInfo>>>> inFlight,
        CancellationToken cancellationToken)
    {
        if (shard.Count == 0)
            return [];

        // Each shard gets its own host, keyed so the pool entry is stable across calls and stays
        // warm for the next batch rather than being rebuilt per request.
        string key = $"{shardIndex} {KeyFor(properties)}";
        var gate = s_gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

        // Counted around the wait as well as the work, and released in a finally that covers both:
        // a load cancelled while queued would otherwise leave the count raised forever, and
        // background warming — which waits on it — would never run again.
        Interlocked.Increment(ref s_inFlightLoads);
        try
        {
        await gate.WaitAsync(cancellationToken);
        try
        {
            // A loader per call, which is cheap — it holds a diagnostic reporter and a file-extension
            // registry, not a process. The manager is what has to survive, and it does — but only
            // when something actually needs evaluating: behind the Lazy, so a shard answered
            // entirely from the evaluation cache never claims a host at all.
            var loader = new MSBuildProjectLoader(workspace, properties);
            var provider = new CachingProjectFileInfoProvider(properties,
                new Lazy<IProjectFileInfoProvider>(() => new BuildHostProjectFileInfoProvider(
                    ManagerFor(key, properties), loader.ProjectFileExtensionRegistry, loader.Reporter,
                    progress: null)),
                inFlight);

            // Only the projects the cache cannot answer are the host's business. Among those, a
            // host that has already evaluated one would answer from its ProjectCollection rather
            // than from disk — see RecycleIfAlreadyEvaluatedAsync. A cache hit must not trigger
            // that recycle: it never touches the host, so the host's copy stays irrelevant.
            var misses = provider.Probe(shard);
            if (misses.Count > 0)
            {
                await RecycleIfAlreadyEvaluatedAsync(key, misses);

                // Saturate the host from the start instead of letting the loader feed it one
                // dependency-ordered project at a time. See Prefetch for why this is the
                // difference between pool size mattering and not.
                provider.Prefetch(misses, cancellationToken);
            }

            // Built inside the shard, so no two concurrent loads ever touch the same map.
            var infos = await loader.LoadInfosAsync(
                [.. shard], provider, projectMapFactory(), progress: null, cancellationToken);

            // Recorded after the fact: every project this host has now evaluated, including the
            // transitive ones it pulled in, because those are cached in its ProjectCollection just
            // as firmly as the ones that were asked for. Only genuine host evaluations count —
            // recording a cache hit here would make the next reload retire a host that never saw
            // the project.
            lock (provider.HostEvaluated)
            {
                if (provider.HostEvaluated.Count > 0)
                {
                    var seen = s_evaluated.GetOrAdd(key,
                        _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    lock (seen)
                    {
                        foreach (string path in provider.HostEvaluated)
                            seen.Add(path);
                    }
                }
            }

            if (provider.Hits > 0)
            {
                string hostWork;
                lock (provider.HostEvaluated)
                {
                    // Named when few, because on a warm load each of these is a surprise worth
                    // being able to read off the log.
                    hostWork = provider.HostEvaluated.Count is > 0 and <= 8
                        ? $"{provider.HostEvaluated.Count} from the host " +
                          $"({string.Join(", ", provider.HostEvaluated.Select(Path.GetFileName))})"
                        : $"{provider.HostEvaluated.Count} from the host";
                }

                Console.Error.WriteLine(
                    $"[BuildHost] Shard {shardIndex}: {provider.Hits} evaluation(s) from cache, {hostWork}");
            }

            return infos;
        }
        finally
        {
            gate.Release();
        }
        }
        finally
        {
            Interlocked.Decrement(ref s_inFlightLoads);
        }
    }

    /// <summary>
    /// Shard loads currently running, so background warming can stay out of their way.
    /// </summary>
    private static int s_inFlightLoads;

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

    private static BuildHostProcessManager ManagerFor(string key, ImmutableDictionary<string, string> properties)
    {
        s_lastUsed[key] = Interlocked.Increment(ref s_useClock);

        var manager = s_managers.GetOrAdd(key, _ => new Lazy<BuildHostProcessManager>(
            () => new BuildHostProcessManager(
                knownCommandLineParserLanguages: [LanguageNames.CSharp, LanguageNames.VisualBasic],
                globalMSBuildProperties: properties),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;

        TrimToCap();
        return manager;
    }

    /// <summary>Monotonic tick, so hosts can be ordered by when they were last asked for.</summary>
    private static long s_useClock;

    private static readonly ConcurrentDictionary<string, long> s_lastUsed = new(StringComparer.Ordinal);

    /// <summary>
    /// The most hosts to keep alive across every property set.
    /// </summary>
    /// <remarks>
    /// The pool is keyed by global properties, and <c>SolutionDir</c> is one of them, so every
    /// solution the process touches wants a pool of its own. That is fine for an editor with one
    /// solution open and not fine for anything that walks through many: each host is a
    /// <c>dotnet</c> process, and since warming makes it parse the whole SDK, each is now expensive
    /// to keep rather than nearly free. Unbounded, a test run that opens dozens of fixture
    /// solutions ends up with dozens of pools and exhausts the machine.
    /// </remarks>
    private static readonly int MaxLiveHosts = PoolSize * 2;

    /// <summary>Retires the least recently used hosts until the pool is back within its cap.</summary>
    private static void TrimToCap()
    {
        while (s_managers.Count > MaxLiveHosts)
        {
            // Oldest by last use, and only among hosts that still exist.
            var victim = s_lastUsed
                .Where(entry => s_managers.ContainsKey(entry.Key))
                .OrderBy(entry => entry.Value)
                .Select(entry => (string?)entry.Key)
                .FirstOrDefault();

            if (victim is null || !s_managers.TryRemove(victim, out var stale))
                return;

            s_lastUsed.TryRemove(victim, out _);
            s_evaluated.TryRemove(victim, out _);

            if (!stale.IsValueCreated)
                continue;

            // Fire and forget: the caller is on the load path waiting for a different host, and a
            // subprocess that is slow to die must not hold it up.
            _ = Task.Run(async () =>
            {
                try { await stale.Value.DisposeAsync(); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[BuildHost] Could not retire an idle host: {ex.Message}");
                }
            });
        }
    }

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
    /// Starts the hosts for <paramref name="properties"/> and pays their MSBuild initialisation
    /// now, so the first project load does not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Connecting a host is not what makes it warm. Measured on one host with four distinct trivial
    /// projects: the manager constructor is free, connecting to the subprocess costs 427 ms, and
    /// then the loads cost <b>956, 234, 239, 264 ms</b>. Nearly a second of the first load is
    /// MSBuild discovering toolsets and SDK resolvers and parsing the SDK's several hundred
    /// <c>.props</c> and <c>.targets</c> into the collection's <c>ProjectRootElementCache</c> —
    /// work that is about the SDK, not about the project, and that every later load reuses.
    /// </para>
    /// <para>
    /// So warming has to make each host <em>evaluate</em> something. It evaluates a synthetic
    /// project in a temp directory rather than one of the solution's: a real project would be
    /// recorded as evaluated and the first genuine request for it would then recycle the very host
    /// this just warmed. The synthetic one names the same SDK and target framework, which is what
    /// decides which targets get parsed.
    /// </para>
    /// <para>
    /// Warming runs one host at a time, and under the same gate real loads take. Both are
    /// deliberate. Warming all four at once was measured <em>slower end to end</em> than not warming
    /// at all — four MSBuild initialisations land on the CPU at the same moment as the request the
    /// editor is waiting on, and the request loses. Taking the gate turns a race into a queue: a
    /// load that arrives mid-warm-up waits for a warm host and then runs at ~235 ms, instead of
    /// racing an initialising one and paying the initialisation twice over.
    /// </para>
    /// <para>
    /// Sequential also means shard 0 — where the first load goes, see <see cref="ShardFor"/> — is
    /// ready first, rather than last-ish behind three hosts nobody needs yet.
    /// </para>
    /// <para>
    /// On a solution nobody goes on to open, the cost is a few idle <c>dotnet</c> processes — which
    /// is why this is called when a solution is explicitly bound, not on every path that happens to
    /// touch this class.
    /// </para>
    /// </remarks>
    public static void WarmInBackground(ImmutableDictionary<string, string> properties, string anyProjectPath)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var watch = Stopwatch.StartNew();
                string? warmupProject = TryCreateWarmupProject(anyProjectPath);

                for (int index = 0; index < PoolSize; index++)
                {
                    await WaitForIdleAsync();

                    string key = $"{index} {KeyFor(properties)}";
                    var gate = s_gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

                    await gate.WaitAsync();
                    try
                    {
                        var manager = ManagerFor(key, properties);
                        await manager.GetBuildHostWithFallbackAsync(anyProjectPath, CancellationToken.None);

                        if (warmupProject is null)
                            continue;

                        // Not routed through LoadShardAsync, which would record the synthetic project
                        // in s_evaluated — harmless in itself, but it muddies the one signal recycling
                        // depends on.
                        var loader = new MSBuildProjectLoader(s_warmupWorkspace.Value, properties);
                        var provider = new BuildHostProjectFileInfoProvider(
                            manager, loader.ProjectFileExtensionRegistry, loader.Reporter, progress: null);

                        await loader.LoadInfosAsync(
                            [warmupProject], provider, ProjectMap.Create(), progress: null, CancellationToken.None);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }

                Console.Error.WriteLine(
                    $"[BuildHost] Warmed {PoolSize} host(s) in {watch.ElapsedMilliseconds} ms; " +
                    "project loads now skip MSBuild's start-up.");
            }
            catch (Exception ex)
            {
                // Nothing awaits this, and the on-demand path creates its own host if this failed.
                Console.Error.WriteLine($"[BuildHost] Warm-up failed: {ex.Message}");
            }
            finally
            {
                CleanUpWarmupDirectory();
            }
        });
    }

    /// <summary>
    /// A workspace for warm-up loads to resolve language services against. Never read from — the
    /// project models the warm-up produces are thrown away; only the parsing they caused inside the
    /// host is wanted.
    /// </summary>
    private static readonly Lazy<Workspace> s_warmupWorkspace =
        new(() => new AdhocWorkspace(WorkspaceService.HostServices), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Blocks until no shard load is running, so warming only ever uses time nobody wanted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the whole difference between warming helping and warming hurting, and it was measured
    /// both ways. Warming four hosts concurrently at bind cost the 25-project benchmark 6,415 ms
    /// against 5,924 ms for not warming at all; warming them one at a time cost 6,749 ms. Neither
    /// was a warming bug — MSBuild initialisation is around a second per host and the first request
    /// arrives about 1.3 s in, so there is no runway, and every millisecond warming spends is taken
    /// from the request the editor is waiting on.
    /// </para>
    /// <para>
    /// Which is a fact about the benchmark, not about the product: it opens a solution and asks for
    /// a code lens immediately, where a person opens a folder and reads for a few seconds first.
    /// Yielding keeps the win in the case that has idle time without paying for it in the case that
    /// does not — the worst outcome becomes a host warmed by the load that needed it, which is
    /// exactly what would have happened anyway.
    /// </para>
    /// </remarks>
    private static async Task WaitForIdleAsync()
    {
        // Bounded, because a server under continuous load would otherwise never warm at all; at
        // the deadline warming proceeds and takes its chances against whatever is running.
        var deadline = Stopwatch.StartNew();
        while (Volatile.Read(ref s_inFlightLoads) > 0 && deadline.Elapsed < TimeSpan.FromSeconds(30))
            await Task.Delay(150);
    }

    private static string? s_warmupDirectory;

    /// <summary>
    /// Writes a throwaway project that names the same SDK and target framework as
    /// <paramref name="anyProjectPath"/>, so evaluating it parses the same targets the real
    /// projects will need.
    /// </summary>
    private static string? TryCreateWarmupProject(string anyProjectPath)
    {
        try
        {
            string? sdk = PathHelper.ReadProjectSdk(anyProjectPath);
            if (sdk is null or "")
                return null;    // Legacy project: no SDK to pre-parse, and a synthetic one would mislead.

            // The framework matters because it selects which targets import. Read rather than
            // assumed: warming net10.0 targets for a net48 solution parses the wrong set.
            string framework = ReadFirstTargetFramework(anyProjectPath) ?? "net8.0";

            string dir = Path.Combine(Path.GetTempPath(), $"roslynsense-warmup-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            s_warmupDirectory = dir;

            string path = Path.Combine(dir, "Warmup.csproj");
            File.WriteAllText(path, $"""
                <Project Sdk="{sdk}">
                  <PropertyGroup>
                    <TargetFramework>{framework}</TargetFramework>
                    <EnableDefaultItems>false</EnableDefaultItems>
                  </PropertyGroup>
                </Project>
                """);

            return path;
        }
        catch
        {
            // Warm-up is an optimisation. A temp directory that cannot be written costs the first
            // real load its speed, not its correctness.
            return null;
        }
    }

    private static string? ReadFirstTargetFramework(string projectPath)
    {
        try
        {
            var match = TargetFrameworkPattern().Match(File.ReadAllText(projectPath));
            if (!match.Success)
                return null;

            // <TargetFrameworks> is a semicolon list; the first is enough to pull the targets in.
            string value = match.Groups["value"].Value.Trim();
            int semicolon = value.IndexOf(';');
            return semicolon < 0 ? value : value[..semicolon].Trim();
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(
        @"<TargetFrameworks?>(?<value>[^<]+)</TargetFrameworks?>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TargetFrameworkPattern();

    private static void CleanUpWarmupDirectory()
    {
        if (Interlocked.Exchange(ref s_warmupDirectory, null) is not { } dir)
            return;

        try { Directory.Delete(dir, recursive: true); } catch { }
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

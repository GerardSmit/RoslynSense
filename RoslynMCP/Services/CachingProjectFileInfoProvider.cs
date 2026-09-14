using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynMCP.Services;

/// <summary>
/// Answers the loader's per-project evaluation requests from <see cref="EvaluationCache"/> when it
/// can, and from the real BuildHost provider when it cannot.
/// </summary>
/// <remarks>
/// <para>
/// This sits at the one seam where a whole solution load funnels down to "evaluate this project":
/// <c>MSBuildProjectLoader.LoadInfosAsync</c> asks its provider for each requested project
/// <em>and</em> each transitive reference it discovers along the way, so wrapping the provider
/// covers the closure without this class having to know anything about graphs.
/// </para>
/// <para>
/// The inner provider is <see cref="Lazy{T}"/> because materialising it is what spins up (or
/// claims) a BuildHost subprocess. On the load this cache exists for — every project fresh in the
/// cache — the lazy is never touched and no BuildHost exists at all.
/// </para>
/// <para>
/// <see cref="HostEvaluated"/> is the provider's confession list: only paths that actually reached
/// the BuildHost belong in the shard's already-evaluated bookkeeping, because only those are held
/// in that host's <c>ProjectCollection</c>. Counting cache hits there would make the next reload
/// recycle a warm host that never saw the project.
/// </para>
/// <para>
/// The <paramref name="inFlight"/> map is shared by every shard of one batch load, and it is the
/// cold-load fix. Each shard's loader walks its own transitive closure, and the closures overlap
/// heavily — measured at 148 evaluation requests for 80 distinct projects — so without it, shards
/// spend most of their parallelism re-deriving each other's answers, which is why doubling the
/// pool barely moved the total. With it, the first shard to ask for a project evaluates it and
/// every other shard awaits that same task. Per batch rather than process-wide on purpose: a
/// reload exists to re-read disk, so nothing may pin an evaluation beyond the load that made it.
/// </para>
/// </remarks>
internal sealed class CachingProjectFileInfoProvider(
    ImmutableDictionary<string, string> properties,
    Lazy<IProjectFileInfoProvider> inner,
    ConcurrentDictionary<string, Lazy<Task<ImmutableArray<ProjectFileInfo>>>> inFlight)
    : IProjectFileInfoProvider
{
    /// <summary>Evaluations reused from disk or another provider in this load, by full project path.</summary>
    private readonly ConcurrentDictionary<string, ImmutableArray<ProjectFileInfo>> _served =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One fingerprint per (project, watched-extension-set) per load. Computing one reads the
    /// project file, the restore assets and a directory listing, and one load asks about the
    /// same project up to three times; scoped to the provider so nothing survives into a later
    /// load, where the files may have changed. The extension set is part of the key because the
    /// entry on disk and a fresh evaluation can disagree about it — when they do, both
    /// fingerprints are real and the disagreement is exactly what forces the re-evaluation.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _fingerprint =
        new(StringComparer.OrdinalIgnoreCase);

    private Func<ImmutableArray<string>, string> FingerprintOf(string projectPath) =>
        extras => _fingerprint.GetOrAdd(
            Path.GetFullPath(projectPath) + "|" + string.Join(";", extras.IsDefault ? [] : extras),
            _ => EvaluationCache.Fingerprint(projectPath, properties, extras));

    /// <summary>Paths the BuildHost genuinely evaluated during this load. Lock to read or write.</summary>
    public HashSet<string> HostEvaluated { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Serializes calls into the BuildHost. The build inside the host is a single MSBuild
    /// <c>BuildManager</c> with one in-process node; two concurrent evaluations is not a
    /// supported shape. The loader never overlapped its calls, but <see cref="Prefetch"/>
    /// deliberately does, so the discipline the loader provided by accident lives here now.
    /// </summary>
    private readonly SemaphoreSlim _hostGate = new(1, 1);

    /// <summary>How many evaluations this provider reused, including shared results from other
    /// providers in this load. This is not the number of physical disk cache reads.</summary>
    public int Hits => _served.Count;

    /// <summary>
    /// Checks the cache for each of <paramref name="projectPaths"/> up front and returns the ones
    /// that will need the BuildHost. Lets the caller make host decisions — recycle or not, spawn
    /// or not — before the loader starts asking.
    /// </summary>
    public List<string> Probe(IEnumerable<string> projectPaths)
    {
        var misses = new List<string>();

        foreach (string path in projectPaths)
        {
            string full = Path.GetFullPath(path);

            // An evaluation someone else in this open already owns — finished or still running —
            // is not a miss: the loader will consume that task, no host here gets involved, and
            // treating it as a miss would recycle a host over a project it is never going to ask.
            // The one exception is a faulted evaluation, which is retired so it can be retried.
            if (inFlight.TryGetValue(full, out var pending))
            {
                if (pending.Value is { IsCompletedSuccessfully: true } done)
                {
                    _served[full] = done.Result;
                    continue;
                }

                if (!pending.Value.IsFaulted && !pending.Value.IsCanceled)
                    continue;

                ((ICollection<KeyValuePair<string, Lazy<Task<ImmutableArray<ProjectFileInfo>>>>>)inFlight)
                    .Remove(new KeyValuePair<string, Lazy<Task<ImmutableArray<ProjectFileInfo>>>>(full, pending));
            }

            if (EvaluationCache.TryGet(path, properties, out var infos, out _, FingerprintOf(path)))
                ShareCacheHit(full, infos);
            else
                misses.Add(path);
        }

        return misses;
    }

    private Lazy<Task<ImmutableArray<ProjectFileInfo>>> ShareCacheHit(
        string full, ImmutableArray<ProjectFileInfo> infos)
    {
        // A warm prewarm used to keep its disk hits only in _served, then return without giving
        // the batch anything to reuse. Publish the same immutable evaluation in the map already
        // shared by the prewarm and all conversion shards, scoped to this one load.
        var cached = new Lazy<Task<ImmutableArray<ProjectFileInfo>>>(() => Task.FromResult(infos));
        while (true)
        {
            var shared = inFlight.GetOrAdd(full, cached);
            if (ReferenceEquals(shared, cached))
            {
                _served[full] = infos;
                return shared;
            }

            // A competing provider may have claimed this project while disk was being read.
            // Adopt that winner, never a different locally read snapshot, and do not start its
            // host work merely by publishing a cache hit: the actual consumer awaits it later.
            if (!shared.IsValueCreated)
                return shared;
            var task = shared.Value;
            if (task.IsCompletedSuccessfully)
            {
                _served[full] = task.Result;
                return shared;
            }
            if (!task.IsFaulted && !task.IsCanceled)
                return shared;

            inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<ImmutableArray<ProjectFileInfo>>>>(full, shared));
        }
    }

    public async Task<ImmutableArray<ProjectFileInfo>> LoadProjectFileInfosAsync(
        string projectPath,
        DiagnosticReportingOptions reportingOptions,
        CancellationToken cancellationToken)
    {
        string full = Path.GetFullPath(projectPath);

        if (_served.TryGetValue(full, out var cached))
            return cached;

        // Lazy inside the map so losing the GetOrAdd race cannot start a second evaluation:
        // exactly one shard runs the factory, every other awaiter shares its task. The token
        // baked in is the winner's, which is fine — all shards of a batch share one anyway.
        var evaluation = inFlight.GetOrAdd(full, _ => new Lazy<Task<ImmutableArray<ProjectFileInfo>>>(
            () => EvaluateAsync(projectPath, full, reportingOptions, cancellationToken)));

        return await evaluation.Value;
    }

    private async Task<ImmutableArray<ProjectFileInfo>> EvaluateAsync(
        string projectPath,
        string full,
        DiagnosticReportingOptions reportingOptions,
        CancellationToken cancellationToken)
    {
        if (EvaluationCache.TryGet(projectPath, properties, out var fromDisk, out _, FingerprintOf(projectPath)))
        {
            _served[full] = fromDisk;
            return fromDisk;
        }

        ImmutableArray<ProjectFileInfo> infos;
        await _hostGate.WaitAsync(cancellationToken);
        try
        {
            var evalWatch = System.Diagnostics.Stopwatch.StartNew();
            infos = await inner.Value.LoadProjectFileInfosAsync(
                projectPath, reportingOptions, cancellationToken);
            if (Environment.GetEnvironmentVariable("ROSLYNMCP_EVAL_TIMING") == "1")
                Console.Error.WriteLine(
                    $"[EvalTiming] {evalWatch.ElapsedMilliseconds} ms {Path.GetFileName(projectPath)}");
        }
        finally
        {
            _hostGate.Release();
        }

        lock (HostEvaluated)
            HostEvaluated.Add(full);

        EvaluationCache.Store(projectPath, properties, infos, OutputsOf(infos), FingerprintOf(projectPath));
        return infos;
    }

    /// <summary>
    /// Starts evaluating <paramref name="projectPaths"/> now, without waiting for the loader to
    /// ask for them one by one.
    /// </summary>
    /// <remarks>
    /// This is the cold-load half of the shard fix. The loader's walk is sequential and
    /// dependency-ordered, so the moment it awaits a project another shard is evaluating, this
    /// shard's host goes idle — which is why a bigger pool barely moved a cold load. Evaluation
    /// itself has no such ordering: MSBuild evaluates each project independently. Queueing every
    /// miss against the host up front keeps it saturated while the loader blocks, and the
    /// loader's own requests then land on the same in-flight tasks. Fire-and-forget is safe
    /// because the loader awaits these very tasks for every path it was asked to load; the
    /// continuation only covers paths the loader decides to skip.
    /// </remarks>
    public void Prefetch(IReadOnlyList<string> projectPaths, CancellationToken cancellationToken)
    {
        // What the loader would use for a skippable project: report and carry on. The first
        // request for a path fixes the options its evaluation runs under; a difference in
        // reporting mode never changes the evaluation itself.
        var reporting = new DiagnosticReportingOptions(
            DiagnosticReportingMode.Log, DiagnosticReportingMode.Log);

        foreach (string path in projectPaths)
        {
            LoadProjectFileInfosAsync(path, reporting, cancellationToken).ContinueWith(
                t => _ = t.Exception,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }
    }

    public async Task<ImmutableArray<string>> GetProjectOutputPathsAsync(
        string projectPath, CancellationToken cancellationToken)
    {
        string full = Path.GetFullPath(projectPath);

        // The loader asks this to resolve a reference to a project it is not loading. An
        // evaluation from this load — finished, in flight on any shard, or on disk — already
        // knows its outputs, so only a project nobody has touched costs a host call.
        if (_served.TryGetValue(full, out var infos))
            return OutputsOf(infos);

        if (inFlight.TryGetValue(full, out var evaluation))
            return OutputsOf(await evaluation.Value);

        if (EvaluationCache.TryGet(projectPath, properties, out infos, out _, FingerprintOf(projectPath)))
        {
            return OutputsOf(await ShareCacheHit(full, infos).Value);
        }

        ImmutableArray<string> paths;
        await _hostGate.WaitAsync(cancellationToken);
        try
        {
            paths = await inner.Value.GetProjectOutputPathsAsync(projectPath, cancellationToken);
        }
        finally
        {
            _hostGate.Release();
        }

        lock (HostEvaluated)
            HostEvaluated.Add(full);

        return paths;
    }

    private static ImmutableArray<string> OutputsOf(ImmutableArray<ProjectFileInfo> infos) =>
        [.. infos
            .SelectMany(i => new[] { i.OutputFilePath, i.OutputRefFilePath })
            .OfType<string>()
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
}

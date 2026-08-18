using RoslynMCP.Tools;

namespace RoslynMCP.Services.Testing;

/// <summary>What a map build did, per test class.</summary>
public sealed record CoverageMapBuildResult(
    TestCoverageMap Map,
    int ClassesRun,
    int ClassesReused,
    IReadOnlyList<string> Failures,
    string? Error = null);

/// <summary>How a map build reports itself while it is going.</summary>
public sealed record CoverageMapProgress(
    string ClassFullName, int Index, int Total, string Status);

/// <summary>
/// Builds the per-test coverage map by running each test class under coverage on its own.
/// </summary>
/// <remarks>
/// One <c>dotnet test</c> process per class is the cost of knowing who covers what — a coverage
/// report merges the whole run into one number per line, so the only way to attribute hits is to
/// narrow what runs. It is paid once and then incrementally: a class whose source has not changed
/// keeps its recorded entry, so a rebuild after editing one test file runs one class.
///
/// The build is paid once, not per class: everything runs <c>--no-build</c> against a single
/// up-front compile. Classes run concurrently when the collector allows it — the profiler-based
/// "Code Coverage" collector instruments in memory and is safe; coverlet rewrites the assemblies
/// on disk for the duration of a run, so under coverlet the classes go one at a time.
/// </remarks>
public static class TestCoverageMapBuilder
{
    /// <summary>"Namespace.Class.Method(args)" — everything before the last dot outside the
    /// parameter list, which is how a test's name names its class.</summary>
    internal static string ClassNameOf(string fullyQualifiedName)
    {
        int cut = fullyQualifiedName.IndexOf('(');
        string withoutArguments = cut >= 0 ? fullyQualifiedName[..cut] : fullyQualifiedName;

        int lastDot = withoutArguments.LastIndexOf('.');
        return lastDot > 0 ? withoutArguments[..lastDot] : withoutArguments;
    }

    public static async Task<CoverageMapBuildResult> BuildAsync(
        string projectPath,
        bool force = false,
        string? classFilter = null,
        int timeoutSecondsPerClass = 300,
        CancellationToken ct = default,
        Action<CoverageMapProgress>? onProgress = null,
        int? maxParallel = null)
    {
        string? csproj = PathHelper.ResolveCsprojPath(projectPath);
        if (csproj is null)
            return Failed($"Could not find a .csproj file for '{projectPath}'.");

        string? solution = PathHelper.FindNearestSolution(csproj);
        if (solution is null)
            return Failed($"'{csproj}' is not inside a solution; the map is stored per solution.");

        var tests = await TestDiscoveryService.DiscoverAsync(csproj, cancellationToken: ct);
        if (tests.Count == 0)
            return Failed($"No tests were discovered in '{Path.GetFileName(csproj)}'.");

        var existing = TestCoverageMapStore.Load(solution);
        var byClass = existing.Entries.ToDictionary(e => e.ClassFullName, StringComparer.Ordinal);

        // Grouped by the class's *full* name: two classes with the same short name in different
        // namespaces are different entries, and the run filter has to name them apart anyway.
        var groups = tests
            .GroupBy(FullClassName, StringComparer.Ordinal)
            .Where(g => classFilter is null
                || g.Key.Contains(classFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        var entries = new List<CoverageMapEntry>();
        var failures = new List<string>();
        int run = 0, reused = 0, completed = 0;
        var gate = new object();

        // Classes outside this project keep whatever the map already holds for them — a build
        // scoped to one test project must not erase another's entries.
        foreach (var entry in existing.Entries)
        {
            if (!string.Equals(entry.ProjectPath, csproj, StringComparison.OrdinalIgnoreCase))
                entries.Add(entry);
        }

        // First pass costs nothing: keep every class whose own source is untouched. What it
        // exercises is unchanged unless the code under it moved — which the staleness check at
        // query time catches.
        var toRun = new List<(string ClassName, string? SourceFile, string? SourceHash, List<string> TestNames)>();
        foreach (var group in groups)
        {
            string className = group.Key;
            string? sourceFile = group.Select(t => t.FilePath).FirstOrDefault(p => p is not null);
            string? sourceHash = sourceFile is null ? null : CoverageMapHash.OfFile(sourceFile);
            var testNames = group.Select(t => t.FullyQualifiedName).Distinct(StringComparer.Ordinal).ToList();

            if (!force
                && byClass.TryGetValue(className, out var previous)
                && previous.SourceHash is { Length: > 0 }
                && string.Equals(previous.SourceHash, sourceHash, StringComparison.Ordinal))
            {
                entries.Add(previous with { Tests = testNames, ProjectPath = csproj });
                reused++;
                completed++;
                onProgress?.Invoke(new CoverageMapProgress(className, completed, groups.Count, "reused"));
                continue;
            }

            toRun.Add((className, sourceFile, sourceHash, testNames));
        }

        if (toRun.Count > 0)
        {
            // One compile for the whole build; every class run below is --no-build against it.
            // Building inside each run made every class pay MSBuild's walk of the project graph.
            var (built, buildOutput) = await BuildProjectTool.TryBuildAsync(
                csproj, "Debug", new BuildWarningsStore(), cancellationToken: ct);
            if (!built)
                return Failed($"The build failed, so nothing was measured: {FirstLine(buildOutput)}");

            bool legacy = PathHelper.RequiresMsBuild(csproj);

            async Task<CoverageResult> RunOneAsync(
                (string ClassName, string? SourceFile, string? SourceHash, List<string> TestNames) item,
                bool dynamicInstrumentation, bool probing, CancellationToken token)
            {
                onProgress?.Invoke(new CoverageMapProgress(
                    item.ClassName, Volatile.Read(ref completed), groups.Count, "running"));

                var result = await CoverageService.CollectAsync(
                    csproj, $"FullyQualifiedName~{item.ClassName}", timeoutSecondsPerClass, token,
                    noBuild: true, dynamicInstrumentation: dynamicInstrumentation);

                // A probe that produced no report at all failed as a collector, not as a test
                // run — record nothing, so the caller can re-run the class under coverlet.
                if (probing && result.NoCoverageFile)
                    return result;

                lock (gate)
                {
                    completed++;

                    if (!result.Success || result.Data is null)
                    {
                        failures.Add($"{item.ClassName}: {FirstLine(result.Message)}");
                        // Better a stale entry than none: dropping it would silently widen every
                        // future impact query to "run everything".
                        if (byClass.TryGetValue(item.ClassName, out var stale))
                            entries.Add(stale with { Tests = item.TestNames, ProjectPath = csproj });
                        onProgress?.Invoke(new CoverageMapProgress(
                            item.ClassName, completed, groups.Count, "failed"));
                    }
                    else
                    {
                        // Every class's pass measures the whole codebase; folded together they are
                        // the aggregate the coverage view shows, at no extra cost over the runs
                        // already made. Inside the lock: Merge is read-modify-write on one file.
                        CoverageSnapshotStore.Merge(csproj, result.Data);

                        entries.Add(new CoverageMapEntry(
                            ClassFullName: item.ClassName,
                            ProjectPath: csproj,
                            Tests: item.TestNames,
                            Files: CoveredFilesFrom(result.Data),
                            SourceFilePath: item.SourceFile,
                            SourceHash: item.SourceHash));

                        run++;
                        onProgress?.Invoke(new CoverageMapProgress(
                            item.ClassName, completed, groups.Count, "recorded"));
                    }
                }

                return result;
            }

            // The first class doubles as a probe of the parallel-safe collector: the dynamic
            // one instruments in memory, but old SDKs cannot write Cobertura from it. If it
            // cannot, that class re-runs under coverlet and the rest stay serial, because
            // coverlet rewrites the assemblies on disk and two runs would corrupt each other.
            // Legacy projects already collect through dotnet-coverage, which is dynamic too.
            bool parallelSafe = legacy;
            if (!legacy)
            {
                var probe = await RunOneAsync(toRun[0], dynamicInstrumentation: true, probing: true, ct);
                if (probe.NoCoverageFile)
                    await RunOneAsync(toRun[0], dynamicInstrumentation: false, probing: false, ct);
                else
                    parallelSafe = true;
                toRun.RemoveAt(0);
            }

            if (parallelSafe)
            {
                int parallelism = maxParallel
                    ?? Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
                await Parallel.ForEachAsync(
                    toRun,
                    new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
                    async (item, token) =>
                        await RunOneAsync(item, dynamicInstrumentation: !legacy, probing: false, token));
            }
            else
            {
                foreach (var item in toRun)
                {
                    ct.ThrowIfCancellationRequested();
                    await RunOneAsync(item, dynamicInstrumentation: false, probing: false, ct);
                }
            }
        }

        var map = new TestCoverageMap(solution, DateTime.UtcNow, entries);
        TestCoverageMapStore.Save(solution, map);

        return new CoverageMapBuildResult(map, run, reused, failures);
    }

    /// <summary>Every line the run actually executed, per file. Lines with no hits are what
    /// this map is not about — an entry records what a test reaches, not what it misses.</summary>
    private static IReadOnlyList<CoveredFile> CoveredFilesFrom(CoverageData data)
    {
        var files = new List<CoveredFile>();

        foreach (var file in data.Files.Values)
        {
            if (string.IsNullOrEmpty(file.FilePath))
                continue;

            var hitLines = file.Lines
                .Where(kv => kv.Value.Hits > 0)
                .Select(kv => kv.Key)
                .ToList();

            if (hitLines.Count == 0)
                continue;

            files.Add(CoveredFile.FromLines(
                file.FilePath, CoverageMapHash.OfFile(file.FilePath), hitLines));
        }

        return files;
    }

    private static string FullClassName(DiscoveredTest test)
    {
        // DiscoveredTest.FullyQualifiedName is "Namespace.Class.Method"; the class name it
        // carries separately is the short one, which is not enough to filter a run.
        int lastDot = test.FullyQualifiedName.LastIndexOf('.');
        return lastDot > 0 ? test.FullyQualifiedName[..lastDot] : test.ClassName;
    }

    private static string FirstLine(string message) =>
        message.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "unknown error";

    private static CoverageMapBuildResult Failed(string error) =>
        new(TestCoverageMap.Empty(""), 0, 0, [], error);
}

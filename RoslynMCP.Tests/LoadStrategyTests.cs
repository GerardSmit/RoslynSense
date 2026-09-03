using System.Diagnostics;
using Microsoft.CodeAnalysis;
using RoslynMCP.Services;
using Xunit;
using Xunit.Abstractions;

namespace RoslynMCP.Tests;

/// <summary>
/// Pins the measurement the batch-loading design rests on: that asking Roslyn for N projects in one
/// call costs dramatically less than asking it N times.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="WorkspaceService.EnsureProjectsLoadedAsync"/> and <see cref="PartialSolution"/> exist
/// only because of the numbers this test measures. Roslyn builds a fresh
/// <c>BuildHostProcessManager</c> — a <c>dotnet</c> subprocess, a cold MSBuild
/// <c>ProjectCollection</c>, a full re-parse of the SDK targets — for every top-level
/// <c>OpenProjectAsync</c> or <c>OpenSolutionAsync</c>, and disposes it when the call returns. So
/// the dominant cost is per call, and the whole trick is to make fewer calls.
/// </para>
/// <para>
/// If that ever stops being true — Roslyn starts pooling build hosts, or the fixed cost falls far
/// enough not to matter — this test fails, and the right response is to delete the batching
/// machinery rather than to relax the assertion. That is why the assertion is a ratio against the
/// per-project strategy measured in the same run on the same machine, and not a millisecond budget:
/// a budget would only ever measure the build agent.
/// </para>
/// </remarks>
public class LoadStrategyTests
{
    private readonly ITestOutputHelper _output;

    public LoadStrategyTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// How much cheaper one batched open must be than the same projects opened one at a time.
    /// Set well below the ~3.8x measured when this was written, so ordinary machine noise and a
    /// loaded build agent cannot fail it, while a regression to per-project cost still can.
    /// </summary>
    private const double RequiredSpeedup = 1.75;

    /// <summary>
    /// Ceiling on either strategy. Not a budget — the assertion is a ratio, deliberately, so that
    /// it measures Roslyn and not the build agent — but a wedged <c>BuildHost</c> makes
    /// <c>OpenProjectAsync</c> wait indefinitely (the same hazard production guards with
    /// <c>ROSLYNMCP_OPEN_PROJECT_TIMEOUT_SECONDS</c>), and a benchmark that hangs for hours tells
    /// you far less than one that fails in five minutes saying which strategy stopped.
    /// </summary>
    private static readonly TimeSpan StrategyCeiling = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How many consumer projects to compare across. Smaller than the stress test's eight: the
    /// per-project strategy is the slow one by construction — this test exists to prove it is slow
    /// — so every extra project costs about 1.6 s of deliberately wasted time, and the ratio is
    /// already decisive at five projects.
    /// </summary>
    private const int ConsumerCount = 4;

    [RoslynSenseBenchFact]
    public async Task LoadingProjectsInOneBatchIsFarCheaperThanOneCallEach()
    {
        WorkspaceService.EnsureRegistered();

        using var solution = LargeSolutionFixture.Create(
            new LargeSolutionOptions(ProjectCount: 6, ConsumerProjectCount: ConsumerCount));

        // Restored up front and timed separately: this test is about Roslyn's per-call cost, and
        // folding NuGet into either strategy would measure the package cache instead.
        var restoreWatch = Stopwatch.StartNew();
        await RunAsync("dotnet", $"restore \"{solution.SolutionPath}\"");
        restoreWatch.Stop();
        _output.WriteLine(
            $"solution-wide restore: {restoreWatch.ElapsedMilliseconds} ms " +
            $"({solution.ProjectPaths.Count} projects)");

        var consumers = solution.ConsumerFiles
            .Select(f => Directory.GetFiles(Path.GetDirectoryName(f)!, "*.csproj").Single())
            .ToList();
        List<string> wanted = [solution.ContractsProjectPath, .. consumers];

        long perProjectMs = await MeasurePerProjectAsync(wanted);
        long batchedMs = await MeasureBatchedAsync(solution.SolutionPath, wanted);

        double speedup = (double)perProjectMs / Math.Max(batchedMs, 1);
        _output.WriteLine(
            $"{wanted.Count} projects: {perProjectMs} ms one call each, {batchedMs} ms batched " +
            $"({speedup:F1}x)");

        Assert.True(speedup >= RequiredSpeedup,
            $"Opening {wanted.Count} projects in one batch took {batchedMs} ms against " +
            $"{perProjectMs} ms one call each — only {speedup:F1}x, under the {RequiredSpeedup:F1}x " +
            "this design is built on. Either Roslyn's per-call BuildHost cost has changed, or the " +
            "batch stopped being a single top-level load.");
    }

    private async Task<long> MeasurePerProjectAsync(IReadOnlyList<string> projects)
    {
        using var workspace = WorkspaceService.CreateWorkspace(TextWriter.Null);
        var watch = Stopwatch.StartNew();
        foreach (string project in projects)
        {
            await workspace.OpenProjectAsync(project)
                .WaitAsync(StrategyCeiling)
                .ConfigureAwait(false);
        }
        watch.Stop();

        Assert.Equal(projects.Count, workspace.CurrentSolution.ProjectIds.Count);
        _output.WriteLine($"  one call each: {watch.ElapsedMilliseconds} ms");
        return watch.ElapsedMilliseconds;
    }

    private async Task<long> MeasureBatchedAsync(string realSolutionPath, IReadOnlyList<string> projects)
    {
        using var partial = PartialSolution.Create(realSolutionPath, projects);
        using var workspace = WorkspaceService.CreateWorkspace(
            TextWriter.Null, extraProperties: partial.GlobalProperties);

        var watch = Stopwatch.StartNew();
        await workspace.OpenSolutionAsync(partial.Path)
            .WaitAsync(StrategyCeiling)
            .ConfigureAwait(false);
        watch.Stop();

        var loaded = workspace.CurrentSolution.Projects.ToList();
        Assert.Equal(projects.Count, loaded.Count);
        _output.WriteLine($"  batched: {watch.ElapsedMilliseconds} ms");

        // Speed is only half of it: a batch that loads faster by producing projects that cannot
        // resolve their own references would pass the timing assertion and be useless. The consumer
        // reaches the generated gRPC client only through its ProjectReference to Contracts, so this
        // is the cross-project edge the graft depends on.
        var consumer = loaded.First(p => p.Name.StartsWith("Consumer", StringComparison.Ordinal));
        var compilation = await consumer.GetCompilationAsync();
        Assert.NotNull(compilation!.GetTypeByMetadataName(
            "LargeSolution.Widgets.WidgetService+WidgetServiceClient"));
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        return watch.ElapsedMilliseconds;
    }

    private static async Task RunAsync(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{file} {args} failed ({process.ExitCode}): {await stderr}");
    }
}

using System.Collections.Immutable;
using RoslynMCP.Services;
using Xunit;
using Sample = RoslynMCP.Services.BuildHostPoolCalibration.Sample;

namespace RoslynMCP.Tests;

/// <summary>
/// The pool-size walk: pick the smallest size within 90% of the best measured throughput, probe
/// one step past an unexplored edge, and fall back to the heuristic without data.
/// </summary>
[Collection(SharedState.Name)]
public class BuildHostPoolCalibrationTests
{
    private static int Choose(ImmutableArray<Sample> samples, int heuristic = 6, int upperBound = 12) =>
        BuildHostPoolCalibration.Choose(samples, heuristic, upperBound);

    [Fact]
    public void NoSamplesMeansTheHeuristic() =>
        Assert.Equal(6, Choose([]));

    [Fact]
    public void ProbesDownFromALoneSample() =>
        // One sample says nothing about whether a smaller pool would do as well, and a host
        // saved is pure win — so the unexplored downward neighbour goes first.
        Assert.Equal(5, Choose([new Sample(6, 6.0, 1)]));

    [Fact]
    public void ProbesUpWhenTheTopOfTheRangeIsStillTheBest() =>
        // 5 hosts measurably lag 6, so the ceiling has not been found yet; the next cold load
        // gets to try 7.
        Assert.Equal(7, Choose([new Sample(5, 4.0, 1), new Sample(6, 6.0, 1)]));

    [Fact]
    public void SettlesAtTheKnee() =>
        // 4 falls off, 5 and 6 are equivalent: both edges are explored, so the walk stops —
        // and within tolerance, fewer hosts win.
        Assert.Equal(5, Choose([new Sample(4, 4.0, 2), new Sample(5, 6.0, 2), new Sample(6, 6.2, 2)]));

    [Fact]
    public void NeverProbesPastTheUpperBound() =>
        Assert.Equal(6, Choose([new Sample(5, 4.0, 1), new Sample(6, 6.0, 1)], upperBound: 6));

    [Fact]
    public void ProbesUpwardFromASinglehostFloor() =>
        // Below 1 there is nothing; the only unexplored neighbour of a lone sample at 1 is 2.
        Assert.Equal(2, Choose([new Sample(1, 5.0, 1)], heuristic: 4));

    /// <summary>
    /// The only test here that touches the disk, and the reason for the collection and the scope.
    /// </summary>
    /// <remarks>
    /// It asserts on what an <em>empty</em> store chooses, so it has to own one. The suite's
    /// sandbox is named after the process id, which Windows recycles — and this test writes a
    /// sample that outlives the run, so a later run landing on a recycled id inherits it and reads
    /// 5 where it expects 4. Every other test in the file is pure and needs none of this.
    /// </remarks>
    [Fact]
    public void RecordsOnlyMeaningfulLoadsAndUsesThem()
    {
        using var store = new EmptyCalibrationStore();

        // Ordered on purpose: both halves share the one calibration file in the sandbox above.
        // A load with too few host evaluations to saturate the pool must not become a sample...
        BuildHostPoolCalibration.Record(poolSize: 6, hostEvaluations: 3, elapsedSeconds: 2.0);
        Assert.Equal(4, BuildHostPoolCalibration.ChoosePoolSize(heuristic: 4, upperBound: 12));

        // ...while a real cold load is recorded, read back, and steers the next choice (a lone
        // sample at 6 probes down to 5).
        BuildHostPoolCalibration.Record(poolSize: 6, hostEvaluations: 24, elapsedSeconds: 4.0);
        Assert.Equal(5, BuildHostPoolCalibration.ChoosePoolSize(heuristic: 4, upperBound: 12));
    }

    /// <summary>A calibration directory of this test's own, deleted when it is done with it.</summary>
    private sealed class EmptyCalibrationStore : IDisposable
    {
        private const string Variable = "ROSLYNMCP_EVAL_CACHE_DIR";

        private readonly string? _previous = Environment.GetEnvironmentVariable(Variable);

        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "roslyn-sense-tests", $"calibration-{Guid.NewGuid():N}");

        public EmptyCalibrationStore()
        {
            Directory.CreateDirectory(_root);
            Environment.SetEnvironmentVariable(Variable, _root);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(Variable, _previous);

            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }
}

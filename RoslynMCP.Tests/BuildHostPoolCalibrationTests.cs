using System.Collections.Immutable;
using RoslynMCP.Services;
using Xunit;
using Sample = RoslynMCP.Services.BuildHostPoolCalibration.Sample;

namespace RoslynMCP.Tests;

/// <summary>
/// The pool-size walk: pick the smallest size within 90% of the best measured throughput, probe
/// one step past an unexplored edge, and fall back to the heuristic without data.
/// </summary>
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

    [Fact]
    public void RecordsOnlyMeaningfulLoadsAndUsesThem()
    {
        // Ordered on purpose: both halves share the one calibration file in the test sandbox.
        // A load with too few host evaluations to saturate the pool must not become a sample...
        BuildHostPoolCalibration.Record(poolSize: 6, hostEvaluations: 3, elapsedSeconds: 2.0);
        Assert.Equal(4, BuildHostPoolCalibration.ChoosePoolSize(heuristic: 4, upperBound: 12));

        // ...while a real cold load is recorded, read back, and steers the next choice (a lone
        // sample at 6 probes down to 5).
        BuildHostPoolCalibration.Record(poolSize: 6, hostEvaluations: 24, elapsedSeconds: 4.0);
        Assert.Equal(5, BuildHostPoolCalibration.ChoosePoolSize(heuristic: 4, upperBound: 12));
    }
}

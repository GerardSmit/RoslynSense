using RoslynMCP.Services.Testing;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Reporting a run while it is still going, and stopping one that is.
/// </summary>
/// <remarks>
/// Against the small <c>DebugTestProject</c> fixture rather than this suite: pointing a nested
/// <c>dotnet test</c> at the assembly hosting the running test cannot relink on Windows.
/// </remarks>
[Collection(SharedState.Name)]
public class TestRunProgressTests
{
    [Fact]
    public async Task OutcomesAreReportedAsEachTestFinishes()
    {
        var events = new List<TestProgress>();

        var outcome = await TestRunService.RunAsync(
            FixturePaths.DebugTestProjectFile,
            filter: null,
            build: true,
            timeoutSeconds: 300,
            cancellationToken: default,
            onProgress: e => { lock (events) events.Add(e); });

        Assert.NotEmpty(outcome.Results);

        List<TestProgress> seen;
        lock (events)
            seen = [.. events];

        var finished = seen.Where(e => e.Kind is "passed" or "failed" or "skipped").ToList();
        Assert.NotEmpty(finished);

        // Every test the TRX reported must also have been announced live; that equivalence is
        // the whole promise of the event channel.
        foreach (var result in outcome.Results)
        {
            Assert.Contains(finished, e =>
                e.FullyQualifiedName is { } name
                && result.FullyQualifiedName.Contains(name.Split('(')[0], StringComparison.Ordinal));
        }

        Assert.Contains(seen, e => e.Kind == "output" && !string.IsNullOrWhiteSpace(e.Message));
    }

    [Fact]
    public void SummaryLinesAreNotMistakenForTestOutcomes()
    {
        // "Passed!  - Failed: 0, Passed: 5, …" and "     Failed: 1" both start with an outcome
        // word. Reading either as a test would invent results that never ran.
        var events = CollectFrom(
            "  Passed DebugTestProject.CalculatorTests.Add_ReturnsSum [12 ms]",
            "  Failed DebugTestProject.CalculatorTests.Broken [3 ms]",
            "  Skipped DebugTestProject.CalculatorTests.Ignored",
            "Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5",
            "     Failed: 1",
            "Test Run Successful.");

        var outcomes = events.Where(e => e.Kind != "output").ToList();

        Assert.Equal(3, outcomes.Count);
        Assert.Equal("passed", outcomes[0].Kind);
        Assert.Equal("DebugTestProject.CalculatorTests.Add_ReturnsSum", outcomes[0].FullyQualifiedName);
        Assert.Equal(12, outcomes[0].DurationMs);
        Assert.Equal("failed", outcomes[1].Kind);
        Assert.Equal("skipped", outcomes[2].Kind);
        Assert.Equal(0, outcomes[2].DurationMs);
    }

    [Fact]
    public void DurationUnitsAreNormalisedToMilliseconds()
    {
        var events = CollectFrom(
            "  Passed A.B.Fast [250 ms]",
            "  Passed A.B.Slow [2 s]",
            "  Passed A.B.Glacial [1.5 m]",
            // vstest writes anything under a millisecond this way; reading it as output rather
            // than an outcome loses every fast test from the live report.
            "  Passed A.B.Instant [< 1 ms]");

        var outcomes = events.Where(e => e.Kind != "output").ToList();

        Assert.Equal(4, outcomes.Count);
        Assert.Equal(250, outcomes[0].DurationMs);
        Assert.Equal(2000, outcomes[1].DurationMs);
        Assert.Equal(90_000, outcomes[2].DurationMs);
        Assert.Equal(1, outcomes[3].DurationMs);
    }

    [Fact]
    public async Task CancellingStopsTheRunAndSaysSo()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = TestRunService.RunAsync(
            FixturePaths.DebugTestProjectFile,
            filter: null,
            build: true,
            timeoutSeconds: 300,
            cancellationToken: cancellation.Token,
            onProgress: _ => started.TrySetResult());

        // Cancel once the process has actually produced output, so this exercises killing a
        // running test host rather than cancelling before anything started.
        await started.Task.WaitAsync(TimeSpan.FromMinutes(2));
        await cancellation.CancelAsync();

        var outcome = await run;

        Assert.Empty(outcome.Results);
        Assert.Equal("Test run cancelled.", outcome.Error);
    }

    /// <summary>Drives the outcome parser over canned console output.</summary>
    private static List<TestProgress> CollectFrom(params string[] lines)
    {
        var events = new List<TestProgress>();
        foreach (string line in lines)
            TestRunService.ReportForTests(events.Add, line);
        return events;
    }
}

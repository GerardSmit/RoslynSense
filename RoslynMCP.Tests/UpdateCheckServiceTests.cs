using System.Diagnostics;
using RoslynMCP.Services;
using Xunit;
using Xunit.Abstractions;

namespace RoslynMCP.Tests;

/// <summary>
/// Covers the update notice. The point of this service is that it costs nothing at startup, so the
/// timing assertions matter as much as the version comparison.
/// </summary>
public class UpdateCheckServiceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("0.1.28", "0.1.27", true)]
    [InlineData("0.2.0", "0.1.99", true)]
    [InlineData("1.0.0", "0.9.9", true)]
    [InlineData("0.1.27", "0.1.27", false)]
    [InlineData("0.1.26", "0.1.27", false)]
    // A prerelease suffix is ignored, so it never reads as newer than the same released version.
    [InlineData("0.1.27-beta", "0.1.27", false)]
    // Unparseable input must not be treated as an update.
    [InlineData("not-a-version", "0.1.27", false)]
    public void WhenComparingVersionsThenOnlyAHigherReleaseCounts(
        string candidate, string current, bool expected) =>
        Assert.Equal(expected, UpdateCheckService.IsNewer(candidate, current));

    [Fact]
    public void WhenBeginCheckCalledThenItReturnsImmediately()
    {
        // The whole reason this exists instead of `dotnet tool update` (~5s even as a no-op) is
        // that it must not delay a session. Any network work happens on a background task.
        var stopwatch = Stopwatch.StartNew();
        UpdateCheckService.BeginCheck();
        stopwatch.Stop();

        output.WriteLine($"BeginCheck took {stopwatch.ElapsedMilliseconds} ms");
        Assert.True(stopwatch.ElapsedMilliseconds < 500,
            $"BeginCheck blocked for {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void WhenHintRequestedThenItNeverBlocks()
    {
        var stopwatch = Stopwatch.StartNew();
        _ = UpdateCheckService.GetHint();
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 100,
            $"GetHint blocked for {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void WhenRunningFromSourceThenTheCurrentVersionIsKnown()
    {
        // Read from assembly metadata, so it is present in a normal build as well as a package.
        output.WriteLine($"CurrentVersion = {UpdateCheckService.CurrentVersion ?? "(null)"}");
        Assert.False(string.IsNullOrWhiteSpace(UpdateCheckService.CurrentVersion));
    }

    [Fact]
    public void WhenDisabledByEnvironmentThenNoHintIsProduced()
    {
        if (!UpdateCheckService.Disabled)
            return; // Enabled in this run; the opt-out path is covered when the variable is set.

        Assert.Null(UpdateCheckService.GetHint());
    }
}

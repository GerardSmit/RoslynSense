using RoslynMCP.Services.Packages;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Feed resolution, and what the caller is told when a feed does not answer.
/// </summary>
/// <remarks>
/// The envelope is the point. Swallowing a feed's failure into a log line makes "this package does
/// not exist" and "the feed holding it rejected your credentials" produce the same empty list, and
/// only one of those is something the user can act on.
/// </remarks>
public class NuGetFeedContextTests
{
    [Fact]
    public void SourcesDescribeEachFeedRatherThanNamingIt()
    {
        var sources = NuGetFeedContext.Sources();

        Assert.All(sources, source =>
        {
            Assert.False(string.IsNullOrWhiteSpace(source.Name));
            Assert.False(string.IsNullOrWhiteSpace(source.Source));
            // A local folder feed and an HTTPS feed behave differently enough that the panel has
            // to be able to tell them apart.
            Assert.Equal(source.IsLocal, !source.Source.StartsWith("http", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public void ConfigFilePathPointsAtAFileThatExists()
    {
        // "Where did this feed come from" is the first question anyone asks about a source they
        // did not add, so a path that does not resolve is worse than none.
        foreach (var source in NuGetFeedContext.Sources())
        {
            if (source.ConfigFilePath is { Length: > 0 } path)
                Assert.True(File.Exists(path), $"{source.Name} names a config file that is not there: {path}");
        }
    }

    [Fact]
    public void RentedCachesAreIndependentAndHonorRefresh()
    {
        using var normal = NuGetFeedContext.RentCache();
        using var refreshed = NuGetFeedContext.RentCache(refresh: true);

        Assert.NotSame(normal, refreshed);
        Assert.False(normal.NoCache);
        // Refresh is what the panel's reload needs: a version published a minute ago is invisible
        // behind the ordinary max-age.
        Assert.True(refreshed.NoCache);
    }

    [Fact]
    public async Task FanOutReportsPerSourceOutcomes()
    {
        var found = await NuGetFeedContext.FanOutAsync<string>(
            packageId: null,
            (repository, _) => Task.FromResult<IEnumerable<string>>([repository.PackageSource.Name]),
            default);

        Assert.Equal(found.Results.Count, found.Feeds.Count);
        Assert.All(found.Feeds, feed => Assert.True(feed.Ok));
    }

    [Fact]
    public async Task AFeedThatThrowsBecomesAnOutcomeNotAnException()
    {
        // One unreachable internal mirror must not empty the results of the feeds that answered.
        var found = await NuGetFeedContext.FanOutAsync<string>(
            packageId: null,
            (_, _) => throw new InvalidOperationException("feed exploded"),
            default);

        Assert.Empty(found.Results);
        Assert.All(found.Feeds, feed =>
        {
            Assert.False(feed.Ok);
            Assert.Contains("exploded", feed.Error ?? "", StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task CancellationPropagatesRatherThanBecomingAFeedFailure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // A cancelled request is the caller moving on, not a broken feed; reporting it as one
        // would put a spurious "feeds did not answer" strip on every keystroke.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NuGetFeedContext.FanOutAsync<string>(
                null,
                (_, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult<IEnumerable<string>>([]); },
                cts.Token));
    }

    [Fact]
    public void InvalidateSurvivesBeingCalledWithNothingLoaded()
    {
        NuGetFeedContext.Invalidate();
        Assert.NotNull(NuGetFeedContext.Sources());
    }
}

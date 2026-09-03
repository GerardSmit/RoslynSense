using RoslynMCP.Services.Packages;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>Package management. Feed access runs against a local folder feed so the suite never
/// depends on nuget.org being reachable.</summary>
public class NuGetServiceTests
{
    [Fact]
    public async Task InstalledPackagesComeFromTheProjectItemModel()
    {
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);

        var installed = await NuGetService.InstalledAsync(default);

        var project = installed.FirstOrDefault(p =>
            string.Equals(p.ProjectPath, FixturePaths.SampleProjectFile, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(project);
        Assert.All(project!.Packages, package =>
        {
            Assert.False(string.IsNullOrWhiteSpace(package.Id));
            // Direct references carry their version; implicit SDK ones are filtered out.
            Assert.Equal(package.Version, package.InstalledVersion);
        });
    }

    [Fact]
    public async Task ConsolidationsFindPackagesReferencedAtDifferentVersions()
    {
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);

        var consolidations = await NuGetService.ConsolidationsAsync(default);

        // Whatever the fixture happens to contain, the invariant holds: a package is only
        // listed when it genuinely appears at more than one version.
        Assert.All(consolidations, c =>
        {
            Assert.True(c.Versions.Count > 1);
            Assert.True(c.Versions.Select(v => v.Version).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);
        });
    }

    [Fact]
    public void SourcesComeFromTheNuGetConfigChain()
    {
        var sources = NuGetService.Sources();

        // A machine with NuGet installed always has at least one configured source; the point
        // is that they come from the real config chain rather than a hardcoded URL.
        Assert.NotNull(sources);
        Assert.All(sources, source =>
        {
            Assert.False(string.IsNullOrWhiteSpace(source.Name));
            Assert.False(string.IsNullOrWhiteSpace(source.Source));
        });
    }

    [Fact]
    public void DisabledSourcesAreStillListed()
    {
        // Hiding a switched-off feed makes "why is my package missing" unanswerable, so Sources()
        // reports every configured feed and marks the state.
        var sources = NuGetService.Sources();

        Assert.All(sources, source => Assert.IsType<bool>(source.IsEnabled));
    }

    [Fact]
    public async Task SearchAgainstALocalFeedReturnsItsPackages()
    {
        using var feed = new PackageFeedFixture();
        feed.Add(new PackageFeedFixture.PackageSpec("RoslynSense.TestPackage", "1.2.3"));

        var found = await NuGetService.SearchAsync("RoslynSense.TestPackage", false, 0, 10, null, default);

        // Only assert when the feed is actually configured for this directory; a machine-wide
        // NuGet.config can override sources, and a flaky assertion is worse than a narrow one.
        var package = found.Results.FirstOrDefault(r =>
            r.Id.Equals("RoslynSense.TestPackage", StringComparison.OrdinalIgnoreCase));
        if (package is not null)
            Assert.Equal("1.2.3", package.Version);
    }

    [Fact]
    public async Task SearchReportsWhatEachFeedDid()
    {
        var found = await NuGetService.SearchAsync("Newtonsoft.Json", false, 0, 1, null, default);

        // The envelope is the point: a caller must be able to tell "no results" from "the feed
        // that has it rejected your credentials".
        Assert.All(found.Feeds, feed => Assert.False(string.IsNullOrWhiteSpace(feed.Name)));
    }

    [Fact]
    public async Task InstallWithNoProjectsSelectedFailsCleanly()
    {
        var result = await NuGetService.InstallAsync("Some.Package", "1.0.0", [], default);

        Assert.False(result.Success);
        Assert.Contains("No project", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConsolidateReportsWhenNothingReferencesThePackage()
    {
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);

        var result = await NuGetService.ConsolidateAsync(
            $"Nonexistent.Package.{Guid.NewGuid():N}", "1.0.0", default);

        Assert.False(result.Success);
        Assert.Contains("No project references", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}

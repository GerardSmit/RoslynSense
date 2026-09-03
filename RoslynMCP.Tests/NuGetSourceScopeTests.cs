using RoslynMCP.Services.Packages;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Answering as though one feed were the only one configured.
/// </summary>
/// <remarks>
/// The panel's source selector used to narrow Browse and nothing else, so picking a private feed
/// still listed packages that feed had never heard of and could offer a "latest" version that only
/// existed somewhere the user had just filtered out. The fix is provenance: the version fan-out
/// already asks every feed, and only the answer to "which one had it" was being discarded.
///
/// Two real directory feeds, so the assertions are about what NuGet actually returns rather than
/// about a stub. A <c>clear</c> element cuts the inherited chain — without it these would query the
/// developer's own configured feeds, and nuget.org has opinions about Newtonsoft.Json that a test
/// should not depend on.
/// </remarks>
[Collection(SharedState.Name)]
public sealed class NuGetSourceScopeTests : IDisposable
{
    private readonly string _root;
    private readonly PackageFeedFixture _alpha = new();
    private readonly PackageFeedFixture _beta = new();

    public NuGetSourceScopeTests()
    {
        // Shared: Alpha stops at 1.1.0, Beta carries the 2.0.0 nobody scoped to Alpha should see.
        _alpha.AddVersions("Shared.Package", "1.0.0", "1.1.0");
        _beta.AddVersions("Shared.Package", "1.0.0", "2.0.0");

        // On Alpha only, which is what "hide what this feed does not carry" has to be tested with.
        _alpha.AddVersions("Alpha.Only", "3.0.0");

        _root = Path.Combine(Path.GetTempPath(), $"nugetscope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "NuGet.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="Alpha" value="{_alpha.Directory}" />
                <add key="Beta" value="{_beta.Directory}" />
              </packageSources>
            </configuration>
            """);

        NuGetFeedContext.SettingsRootOverride = _root;
        NuGetFeedContext.Invalidate();
        PackageUpdateService.Invalidate();
    }

    [Fact]
    public async Task AVersionListingRemembersWhichFeedServedEachVersion()
    {
        var found = await NuGetService.AllVersionsBySourceAsync(
            "Shared.Package", includePrerelease: false, refresh: true, default);

        Assert.Equal(
            ["1.0.0", "1.1.0"],
            found.Results.Where(v => v.Source == "Alpha")
                .Select(v => v.Version.ToNormalizedString()).Order());

        Assert.Equal(
            ["1.0.0", "2.0.0"],
            found.Results.Where(v => v.Source == "Beta")
                .Select(v => v.Version.ToNormalizedString()).Order());
    }

    /// <summary>
    /// Scoped to Alpha, the newest version is Alpha's newest. Offering Beta's 2.0.0 here is the bug
    /// this exists to prevent: it would install a package the selected feed cannot restore.
    /// </summary>
    [Fact]
    public async Task NarrowingToOneFeedNarrowsTheCandidateVersions()
    {
        var found = await NuGetService.AllVersionsBySourceAsync(
            "Shared.Package", includePrerelease: false, refresh: true, default);

        Assert.Equal("1.1.0", NuGetService.Distinct(found.Results, "Alpha")[0].ToNormalizedString());
        Assert.Equal("2.0.0", NuGetService.Distinct(found.Results, "Beta")[0].ToNormalizedString());

        // No source is every source, which is not the same as no versions at all.
        Assert.Equal("2.0.0", NuGetService.Distinct(found.Results)[0].ToNormalizedString());
    }

    [Fact]
    public async Task APackageTheFeedDoesNotCarryHasNoVersionsOnIt()
    {
        var found = await NuGetService.AllVersionsBySourceAsync(
            "Alpha.Only", includePrerelease: false, refresh: true, default);

        Assert.Empty(NuGetService.Distinct(found.Results, "Beta"));
        Assert.Single(NuGetService.Distinct(found.Results, "Alpha"));
    }

    /// <summary>
    /// What the Installed tab filters on: the feeds an id exists on at all. Shared by the update
    /// check's cache, so asking after one costs nothing.
    /// </summary>
    [Fact]
    public async Task TheSourceMapNamesEveryFeedCarryingAPackage()
    {
        var map = await PackageUpdateService.SourcesOfAsync(
            ["Shared.Package", "Alpha.Only", "Nowhere.Package"], default);

        Assert.Equal(["Alpha", "Beta"], map["Shared.Package"]);
        Assert.Equal(["Alpha"], map["Alpha.Only"]);
        Assert.Empty(map["Nowhere.Package"]);
    }

    public void Dispose()
    {
        NuGetFeedContext.SettingsRootOverride = null;
        NuGetFeedContext.Invalidate();
        PackageUpdateService.Invalidate();
        _alpha.Dispose();
        _beta.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}

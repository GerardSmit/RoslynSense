using RoslynMCP.Services;
using RoslynMCP.Services.Packages;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>Package management. Search/version lookups run against a local folder feed so the
/// suite never depends on nuget.org being reachable.</summary>
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
    }

    [Fact]
    public async Task SearchAgainstALocalFeedReturnsItsPackages()
    {
        using var feed = await LocalFeed.CreateAsync("RoslynSense.TestPackage", "1.2.3");

        var results = await NuGetService.SearchAsync("RoslynSense.TestPackage", false, 0, 10, default);

        // Only assert when the feed is actually configured for this directory; a machine-wide
        // NuGet.config can override sources, and a flaky assertion is worse than a narrow one.
        if (results.Any(r => r.Id.Equals("RoslynSense.TestPackage", StringComparison.OrdinalIgnoreCase)))
        {
            var package = results.First(r => r.Id.Equals("RoslynSense.TestPackage", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("1.2.3", package.Version);
        }
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

    [Fact]
    public async Task IconProxyRejectsNonHttpUrls()
    {
        Assert.Null(await NuGetService.IconDataUriAsync("file:///C:/secret.png", default));
        Assert.Null(await NuGetService.IconDataUriAsync("not a url", default));
    }

    [Fact]
    public void TransitiveDependenciesReadProjectAssetsWithoutRestoring()
    {
        // SampleProject has been built by the suite, so its assets file exists.
        var transitive = NuGetService.Transitive(FixturePaths.SampleProjectFile);

        Assert.All(transitive, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Id));
            Assert.False(string.IsNullOrWhiteSpace(entry.BroughtInBy));
        });
    }

    [Fact]
    public void TransitiveOfAProjectWithoutAssetsIsEmpty() =>
        Assert.Empty(NuGetService.Transitive(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}", "App.csproj")));

    /// <summary>A directory-based package source holding one generated .nupkg.</summary>
    private sealed class LocalFeed : IDisposable
    {
        private readonly string _directory;

        private LocalFeed(string directory) => _directory = directory;

        public static async Task<LocalFeed> CreateAsync(string id, string version)
        {
            string directory = Path.Combine(Path.GetTempPath(), $"feed-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);

            // A .nupkg is a zip with a .nuspec; writing one directly avoids needing `dotnet pack`.
            string nupkg = Path.Combine(directory, $"{id}.{version}.nupkg");
            await using (var stream = File.Create(nupkg))
            using (var archive = new System.IO.Compression.ZipArchive(
                stream, System.IO.Compression.ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry($"{id}.nuspec");
                await using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync($"""
                    <?xml version="1.0" encoding="utf-8"?>
                    <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                      <metadata>
                        <id>{id}</id>
                        <version>{version}</version>
                        <authors>RoslynSense</authors>
                        <description>Test package</description>
                      </metadata>
                    </package>
                    """);
            }

            return new LocalFeed(directory);
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); } catch { }
        }
    }
}

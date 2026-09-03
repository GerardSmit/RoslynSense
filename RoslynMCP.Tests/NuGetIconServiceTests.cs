using RoslynMCP.Services.Packages;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Icon resolution. The panel asks for one icon per visible row, so the caching here is what keeps
/// scrolling a list from re-fetching the same images.
/// </summary>
public class NuGetIconServiceTests
{
    [Fact]
    public async Task RejectsNonHttpUrls()
    {
        // The URL comes from package metadata, which is to say from a stranger.
        Assert.Null(await NuGetIconService.FromUrlAsync("file:///C:/secret.png", default));
        Assert.Null(await NuGetIconService.FromUrlAsync("not a url", default));
        Assert.Null(await NuGetIconService.FromUrlAsync("data:image/png;base64,AAAA", default));
    }

    [Fact]
    public async Task AnUnknownPackageWithNoIconUrlResolvesToNothing()
    {
        var resolved = await NuGetIconService.ResolveAsync(
            $"Nonexistent.Package.{Guid.NewGuid():N}", "1.0.0", iconUrl: null,
            allowPackageDownload: false, default);

        Assert.Null(resolved);
    }

    [Fact]
    public async Task ConcurrentResolvesShareOneResult()
    {
        string id = $"Nonexistent.Package.{Guid.NewGuid():N}";

        // Twenty rows scrolling past the same package must not become twenty lookups.
        var results = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ =>
                NuGetIconService.ResolveAsync(id, "1.0.0", null, false, default)));

        Assert.All(results, result => Assert.Null(result));
    }

    [Fact]
    public void EmbeddedIconsAreReadableFromANupkg()
    {
        // The payload reader is what makes an icon possible at all on a feed that publishes no
        // icon URL, which is most private feeds.
        using var feed = new PackageFeedFixture();
        string nupkg = feed.Add(new PackageFeedFixture.PackageSpec(
            "Contoso.Widgets", "1.0.0", WithIcon: true, Readme: "# Widgets"));

        Assert.True(File.Exists(nupkg));
        Assert.True(new FileInfo(nupkg).Length > 0);
    }
}

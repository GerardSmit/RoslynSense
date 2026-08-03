using System.Security.Cryptography;
using System.Text;
using RoslynMCP.Config;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The parts of Source Link resolution that do not need a network: reading the document map a
/// PDB carries, and deciding whether what came back is what was compiled.
/// </summary>
[Collection(SharedState.Name)]
public class SourceLinkTests
{
    private const string Map = """
        {
          "documents": {
            "C:\\src\\*": "https://raw.githubusercontent.com/owner/repo/abc123/*",
            "C:\\src\\submodule\\*": "https://raw.githubusercontent.com/owner/sub/def456/*"
          }
        }
        """;

    [Fact]
    public void DocumentPathsMapOntoTheirUrl()
    {
        string? url = SourceLinkService.ResolveUrl(Map, @"C:\src\Widgets\Order.cs");

        Assert.Equal("https://raw.githubusercontent.com/owner/repo/abc123/Widgets/Order.cs", url);
    }

    [Fact]
    public void TheMostSpecificPrefixWins()
    {
        // Both patterns match; a repository with submodules relies on the longer one being
        // chosen, or every submodule file resolves to the parent repository and 404s.
        string? url = SourceLinkService.ResolveUrl(Map, @"C:\src\submodule\Deep\Thing.cs");

        Assert.Equal("https://raw.githubusercontent.com/owner/sub/def456/Deep/Thing.cs", url);
    }

    [Fact]
    public void SeparatorsBecomeUrlSeparators()
    {
        string? url = SourceLinkService.ResolveUrl(Map, @"C:\src\a\b\c.cs");

        Assert.NotNull(url);
        Assert.DoesNotContain('\\', url!);
    }

    [Fact]
    public void AnUnmappedPathResolvesToNothing()
    {
        Assert.Null(SourceLinkService.ResolveUrl(Map, @"D:\elsewhere\Order.cs"));
        Assert.Null(SourceLinkService.ResolveUrl("""{"nothing": {}}""", @"C:\src\Order.cs"));
    }

    [Fact]
    public void ContentIsAcceptedOnlyWhenItHashesToWhatThePdbRecorded()
    {
        byte[] content = Encoding.UTF8.GetBytes("class Order { }");

        Assert.True(SourceLinkService.Matches(
            content, SHA256.HashData(content), SourceLinkService.Sha256));
        Assert.True(SourceLinkService.Matches(
            content, SHA1.HashData(content), SourceLinkService.Sha1));

        // Different content, an unknown algorithm, and a missing hash all have to fail: source
        // that does not match the assembly puts breakpoints on the wrong lines.
        Assert.False(SourceLinkService.Matches(
            Encoding.UTF8.GetBytes("class Order { int x; }"),
            SHA256.HashData(content),
            SourceLinkService.Sha256));
        Assert.False(SourceLinkService.Matches(content, SHA256.HashData(content), Guid.NewGuid()));
        Assert.False(SourceLinkService.Matches(content, [], SourceLinkService.Sha256));
    }

    [Fact]
    public async Task NothingIsFetchedWhenTheSettingIsOff()
    {
        bool original = LspFeatureOptions.SourceLink;
        try
        {
            LspFeatureOptions.SourceLink = false;

            // A metadata symbol — the only kind this is ever asked about.
            var symbol = await RoslynTestHelpers.GetNamedTypeAsync(
                FixturePaths.SampleProjectFile, "System.String");
            var project = await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);

            Assert.Null(await SourceLinkService.TryResolveAsync(symbol, project, default));
        }
        finally
        {
            LspFeatureOptions.SourceLink = original;
        }
    }
}

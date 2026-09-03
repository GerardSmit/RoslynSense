using RoslynMCP.Config;
using RoslynMCP.Languages;
using RoslynMCP.Languages.WebConfig;
using RoslynMCP.Languages.WebConfig.Core;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Which names the web.config pack claims, including the ones <c>webConfig.additionalFiles</c>
/// adds.
/// </summary>
/// <remarks>
/// In the serialized collection because the claimed set is process-wide — the document cache and
/// the watched-file filter are static and read it, so a test that configures it would otherwise
/// race the ones asserting on the defaults.
/// </remarks>
[Collection(SharedState.Name)]
public class WebConfigFileNameTests : IDisposable
{
    public void Dispose() => WebConfigFile.Configure([]);

    [Theory]
    [InlineData("web.config", true)]
    [InlineData("Web.config", true)]
    [InlineData("app.config", true)]
    [InlineData("Web.Release.config", false)]
    [InlineData("packages.config", false)]
    [InlineData("nuget.config", false)]
    public void ClassifiesConfigFileNames(string name, bool owned) =>
        Assert.Equal(owned, WebConfigFile.IsConfigPath(name));

    [Fact]
    public void TheFileMapRoutesByName()
    {
        var map = new LanguageFileMap([new WebConfigLanguage()]);

        Assert.NotNull(map.Resolve(@"C:\site\web.config"));
        Assert.NotNull(map.Resolve(@"C:\site\Admin\Web.config"));
        Assert.NotNull(map.Resolve(@"C:\app\app.config"));
        Assert.Null(map.Resolve(@"C:\site\Web.Release.config"));
        Assert.Null(map.Resolve(@"C:\site\packages.config"));
    }

    [Fact]
    public void ConfiguredNamesAreClaimedAndRoutedLikeTheBuiltInOnes()
    {
        WebConfigFile.Configure(["release.config", "development.config"]);
        var map = new LanguageFileMap([new WebConfigLanguage()]);

        Assert.True(WebConfigFile.IsConfigPath(@"C:\site\release.config"));
        Assert.True(WebConfigFile.IsConfigPath(@"C:\site\Development.config"));
        Assert.NotNull(map.Resolve(@"C:\site\release.config"));
        Assert.NotNull(map.Resolve(@"C:\site\Development.config"));

        // The built-ins survive being added to, and the ones that were never ours stay out.
        Assert.NotNull(map.Resolve(@"C:\site\web.config"));
        Assert.Null(map.Resolve(@"C:\site\packages.config"));
    }

    [Fact]
    public void ConfiguringNothingRestoresTheBuiltInSet()
    {
        WebConfigFile.Configure(["release.config"]);
        WebConfigFile.Configure([]);

        Assert.Equal(WebConfigFile.BuiltInNames, WebConfigFile.Names);
        Assert.False(WebConfigFile.IsConfigPath(@"C:\site\release.config"));
    }

    /// <summary>
    /// An additional file answers for itself and joins no chain: the chain is what a nested
    /// <c>web.config</c> builds, and a sibling under another name is not a nearer version of it.
    /// </summary>
    [Fact]
    public void ConfiguredNamesDoNotJoinTheOverrideChain()
    {
        WebConfigFile.Configure(["release.config"]);
        string dir = Path.Combine(
            Path.GetTempPath(), "roslynsense-webconfig-names-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            File.WriteAllText(Path.Combine(dir, "release.config"), "<configuration />");
            Assert.Null(WebConfigFile.Locate(dir));

            File.WriteAllText(Path.Combine(dir, "web.config"), "<configuration />");
            Assert.EndsWith("web.config", WebConfigFile.Locate(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

/// <summary>
/// What <c>webConfig.additionalFiles</c> resolves to before it ever reaches the pack: the names
/// that survive, and a warning for every one that does not.
/// </summary>
public class WebConfigAdditionalFilesResolutionTests
{
    private static string[] Resolve(
        out List<string> warnings, bool packEnabled = true, params string[] declared)
    {
        var config = new RoslynSenseConfig
        {
            Tools = new ToolsConfig { WebConfig = packEnabled },
            WebConfig = new WebConfigConfig { AdditionalFiles = declared },
        };

        return [.. EffectiveSettings.Resolve([], config, out warnings).WebConfigFiles];
    }

    [Fact]
    public void KeepsTheDeclaredNames()
    {
        var files = Resolve(out var warnings, true, "release.config", "development.config");

        Assert.Equal(["release.config", "development.config"], files);
        Assert.Empty(warnings);
    }

    [Fact]
    public void DropsTheBuiltInsWithoutComplaining()
    {
        var files = Resolve(out var warnings, true, "web.config", "App.config", "release.config");

        Assert.Equal(["release.config"], files);
        Assert.Empty(warnings);
    }

    [Fact]
    public void DeduplicatesCaseInsensitively()
    {
        var files = Resolve(out _, true, "release.config", "Release.config");

        Assert.Equal(["release.config"], files);
    }

    [Theory]
    [InlineData(@"config\release.config")]
    [InlineData("*.config")]
    [InlineData("release.?onfig")]
    public void RejectsPathsAndGlobs(string declared)
    {
        var files = Resolve(out var warnings, true, declared);

        Assert.Empty(files);
        Assert.Contains(warnings, w => w.Contains(declared) && w.Contains("not a path or a glob"));
    }

    /// <summary>
    /// Claiming by name rather than by extension is exactly what keeps NuGet's two files NuGet's;
    /// a config file must not be able to undo that from the outside.
    /// </summary>
    [Theory]
    [InlineData("packages.config")]
    [InlineData("NuGet.Config")]
    public void RejectsTheNuGetNames(string declared)
    {
        var files = Resolve(out var warnings, true, declared);

        Assert.Empty(files);
        Assert.Contains(warnings, w => w.Contains("NuGet"));
    }

    [Fact]
    public void SaysSoWhenTheNamesCannotTakeEffect()
    {
        var files = Resolve(out var warnings, false, "release.config");

        Assert.Empty(files);
        Assert.Contains(warnings, w => w.Contains("the web.config pack is off"));
    }
}

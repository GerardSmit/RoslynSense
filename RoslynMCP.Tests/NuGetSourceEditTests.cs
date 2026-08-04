using RoslynMCP.Services.Packages;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Adding, retargeting, removing, disabling and reordering feeds.
/// </summary>
/// <remarks>
/// These write a real NuGet.config, so the whole class runs against a throwaway directory with a
/// <c>clear</c> element — otherwise a test run would edit the developer's own machine
/// configuration, which is the one failure mode that would not stay inside the test.
/// </remarks>
[Collection(SharedState.Name)]
public sealed class NuGetSourceEditTests : IDisposable
{
    private readonly string _root;

    public NuGetSourceEditTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"nugetcfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        // <clear /> cuts the inherited chain, so the machine's real feeds are neither read nor
        // written by anything below.
        File.WriteAllText(Path.Combine(_root, "NuGet.config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="Alpha" value="https://alpha.invalid/v3/index.json" />
                <add key="Beta" value="https://beta.invalid/v3/index.json" />
              </packageSources>
            </configuration>
            """);

        NuGetFeedContext.SettingsRootOverride = _root;
        NuGetFeedContext.Invalidate();
    }

    [Fact]
    public void AddWritesTheFeedAndItComesBackEnabled()
    {
        var result = NuGetFeedContext.AddSource("Gamma", "https://gamma.invalid/v3/index.json");

        Assert.True(result.Success, result.Message);
        var added = Find("Gamma");
        Assert.Equal("https://gamma.invalid/v3/index.json", added.Source);
        Assert.True(added.IsEnabled);
    }

    [Fact]
    public void AddRefusesADuplicateName()
    {
        var result = NuGetFeedContext.AddSource("Alpha", "https://elsewhere.invalid/v3/index.json");

        Assert.False(result.Success);
        Assert.Contains("already exists", result.Message, StringComparison.OrdinalIgnoreCase);
        // And the original is untouched.
        Assert.Equal("https://alpha.invalid/v3/index.json", Find("Alpha").Source);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://nope.invalid")]
    [InlineData("just some text")]
    public void AddRefusesSomethingThatIsNeitherAUrlNorAFolder(string source)
    {
        // A feed that cannot resolve fails every search with a protocol error rather than saying
        // what is wrong, so it is rejected at the point it is typed.
        var result = NuGetFeedContext.AddSource("Bad", source);

        Assert.False(result.Success);
        Assert.Null(NuGetFeedContext.Sources().FirstOrDefault(s => s.Name == "Bad"));
    }

    [Fact]
    public void ALocalFolderIsAValidFeed()
    {
        using var feed = new PackageFeedFixture();

        Assert.True(NuGetFeedContext.AddSource("Local", feed.Directory).Success);
        Assert.True(Find("Local").IsLocal);
    }

    [Fact]
    public void UpdateRetargetsAFeedInPlace()
    {
        Assert.True(NuGetFeedContext.UpdateSource("Alpha", null, "https://moved.invalid/v3/index.json").Success);

        Assert.Equal("https://moved.invalid/v3/index.json", Find("Alpha").Source);
    }

    [Fact]
    public void UpdateCanRenameWithoutLosingThePosition()
    {
        var before = NuGetFeedContext.Sources().Select(s => s.Name).ToList();

        Assert.True(NuGetFeedContext.UpdateSource("Alpha", "Alpha.Renamed", null).Success);

        var after = NuGetFeedContext.Sources().Select(s => s.Name).ToList();
        Assert.Equal(before.IndexOf("Alpha"), after.IndexOf("Alpha.Renamed"));
        Assert.DoesNotContain("Alpha", after);
    }

    [Fact]
    public void UpdateRefusesToRenameOntoAnExistingFeed()
    {
        var result = NuGetFeedContext.UpdateSource("Alpha", "Beta", null);

        Assert.False(result.Success);
        Assert.Contains("already exists", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemoveDropsTheFeed()
    {
        Assert.True(NuGetFeedContext.RemoveSource("Beta").Success);

        Assert.DoesNotContain(NuGetFeedContext.Sources(), s => s.Name == "Beta");
    }

    [Fact]
    public void EditingAFeedThatIsNotThereSaysSoRatherThanCreatingIt()
    {
        Assert.False(NuGetFeedContext.RemoveSource("Nope").Success);
        Assert.False(NuGetFeedContext.UpdateSource("Nope", "Other", null).Success);
        Assert.False(NuGetFeedContext.SetSourceEnabled("Nope", false).Success);
        Assert.DoesNotContain(NuGetFeedContext.Sources(), s => s.Name is "Nope" or "Other");
    }

    [Fact]
    public void DisablingKeepsTheFeedListedSoItCanBeTurnedBackOn()
    {
        Assert.True(NuGetFeedContext.SetSourceEnabled("Beta", false).Success);

        var disabled = Find("Beta");
        Assert.False(disabled.IsEnabled);

        Assert.True(NuGetFeedContext.SetSourceEnabled("Beta", true).Success);
        Assert.True(Find("Beta").IsEnabled);
    }

    [Fact]
    public void ADisabledFeedIsNotQueried()
    {
        Assert.True(NuGetFeedContext.SetSourceEnabled("Beta", false).Success);

        var queried = NuGetFeedContext.Repositories().Select(r => r.PackageSource.Name).ToList();

        Assert.Contains("Alpha", queried);
        Assert.DoesNotContain("Beta", queried);
    }

    [Fact]
    public void ReorderChangesWhichFeedIsConsultedFirst()
    {
        // Order is not cosmetic: it decides which feed a package published to both resolves from.
        Assert.True(NuGetFeedContext.ReorderSources(["Beta", "Alpha"]).Success);

        Assert.Equal(["Beta", "Alpha"], NuGetFeedContext.Sources().Select(s => s.Name));
    }

    [Fact]
    public void ReorderKeepsFeedsTheCallerDidNotMention()
    {
        Assert.True(NuGetFeedContext.AddSource("Gamma", "https://gamma.invalid/v3/index.json").Success);

        // The panel could be a moment out of date; a feed it has not heard of must not vanish.
        Assert.True(NuGetFeedContext.ReorderSources(["Beta"]).Success);

        var names = NuGetFeedContext.Sources().Select(s => s.Name).ToList();
        Assert.Equal("Beta", names[0]);
        Assert.Contains("Alpha", names);
        Assert.Contains("Gamma", names);
    }

    [Fact]
    public void ReorderKeepsEveryAttributeOnTheFeedsItMoves()
    {
        // A reorder rewrites the section, so anything it forgets to carry over is silently lost —
        // and losing allowInsecureConnections breaks an internal HTTP feed on an unrelated edit.
        File.WriteAllText(Path.Combine(_root, "NuGet.config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="Alpha" value="http://alpha.invalid/v3/index.json" protocolVersion="3" allowInsecureConnections="true" />
                <add key="Beta" value="https://beta.invalid/v3/index.json" />
              </packageSources>
            </configuration>
            """);
        NuGetFeedContext.Invalidate();

        Assert.True(NuGetFeedContext.ReorderSources(["Beta", "Alpha"]).Success);

        string written = File.ReadAllText(Path.Combine(_root, "NuGet.config"));
        Assert.Contains("""allowInsecureConnections="true" """.TrimEnd(), written);
        Assert.Contains("""protocolVersion="3" """.TrimEnd(), written);
        Assert.Equal(["Beta", "Alpha"], NuGetFeedContext.Sources().Select(s => s.Name));
    }

    [Fact]
    public void AnEditIsVisibleToTheNextQueryWithoutAReload()
    {
        Assert.True(NuGetFeedContext.AddSource("Gamma", "https://gamma.invalid/v3/index.json").Success);

        // Invalidate() runs inside the mutation, so the cached repositories cannot serve a stale set.
        Assert.Contains(
            NuGetFeedContext.Repositories(),
            repository => repository.PackageSource.Name == "Gamma");
    }

    private static PackageSourceInfo Find(string name)
    {
        var source = NuGetFeedContext.Sources().FirstOrDefault(s => s.Name == name);
        Assert.True(source is not null, $"'{name}' was not among the configured feeds.");
        return source!;
    }

    public void Dispose()
    {
        NuGetFeedContext.SettingsRootOverride = null;
        NuGetFeedContext.Invalidate();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}

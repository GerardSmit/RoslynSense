using RoslynMCP.Config;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// How the layers merge: which files are consulted, in which order, and what a nearer one is
/// allowed to override.
/// </summary>
/// <remarks>
/// In the serialized collection because the home directory is an environment variable, and the
/// process has only one environment.
/// </remarks>
[Collection(SharedState.Name)]
public sealed class ConfigLayerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "roslynsense-layers-" + Guid.NewGuid().ToString("N"));

    private readonly string? _previousHome =
        Environment.GetEnvironmentVariable(ConfigPaths.HomeOverrideVariable);

    private string Home => Path.Combine(_root, "home");

    public ConfigLayerTests()
    {
        Directory.CreateDirectory(Home);
        Environment.SetEnvironmentVariable(ConfigPaths.HomeOverrideVariable, Home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ConfigPaths.HomeOverrideVariable, _previousHome);
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string Dir(params string[] segments)
    {
        string path = Path.Combine([_root, .. segments]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Write(string directory, string fileName, string json) =>
        File.WriteAllText(Path.Combine(directory, fileName), json);

    [Fact]
    public void NoFilesAnywhereIsNoConfiguration()
    {
        var layered = RoslynSenseConfigLoader.LoadLayers(Dir("repo"));

        Assert.Null(layered.Config);
        Assert.Null(layered.PrimaryPath);
        Assert.Null(layered.LoadError);
        Assert.Empty(layered.Present);
    }

    [Fact]
    public void ANearerFileOverridesOnlyTheFieldsItNames()
    {
        string repo = Dir("repo");
        string nested = Dir("repo", "src");
        Write(repo, "roslynsense.json", """
            { "tools": { "webForms": false }, "tableFormat": "toon" }
            """);
        Write(nested, "roslynsense.json", """
            { "tableFormat": "markdown" }
            """);

        var config = RoslynSenseConfigLoader.LoadLayers(nested).Config;

        Assert.NotNull(config);
        Assert.False(config.Tools.WebForms);      // inherited from the outer file
        Assert.Equal("markdown", config.TableFormat);
    }

    [Fact]
    public void TheGlobalFileIsTheWeakestLayer()
    {
        string repo = Dir("repo");
        Write(Home, "roslynsense.json", """
            { "tableFormat": "toon", "maxWorkspaces": 8 }
            """);
        Write(repo, "roslynsense.json", """
            { "tableFormat": "markdown" }
            """);

        var config = RoslynSenseConfigLoader.LoadLayers(repo).Config;

        Assert.NotNull(config);
        Assert.Equal("markdown", config.TableFormat);
        Assert.Equal(8, config.MaxWorkspaces);
    }

    [Fact]
    public void TheLocalFileBeatsTheCommittedOneBesideIt()
    {
        string repo = Dir("repo");
        Write(repo, "roslynsense.json", """
            { "tools": { "webForms": true, "razor": true } }
            """);
        Write(repo, "roslynsense.local.json", """
            { "tools": { "razor": false } }
            """);

        var config = RoslynSenseConfigLoader.LoadLayers(repo).Config;

        Assert.NotNull(config);
        Assert.True(config.Tools.WebForms);
        Assert.False(config.Tools.Razor);
    }

    [Fact]
    public void ThePersonalFileInTheHomeDirectoryBeatsEverything()
    {
        string repo = Dir("repo");
        Write(repo, "roslynsense.json", """
            { "tableFormat": "markdown" }
            """);
        Write(repo, "roslynsense.local.json", """
            { "tableFormat": "markdown" }
            """);

        string personal = Path.GetDirectoryName(ConfigPaths.PersonalConfigFile(repo)!)!;
        Directory.CreateDirectory(personal);
        Write(personal, "roslynsense.json", """
            { "tableFormat": "toon" }
            """);

        var layered = RoslynSenseConfigLoader.LoadLayers(repo);

        Assert.Equal("toon", layered.Config!.TableFormat);
        Assert.Equal(ConfigScope.Personal, layered.Present.Last().Scope);
    }

    /// <summary>
    /// Absent and "set to the value the default happens to be" are different things, which is the
    /// whole reason the merge runs on the JSON rather than on the resolved object.
    /// </summary>
    [Fact]
    public void ASettingNoLayerMentionsKeepsItsDefault()
    {
        string repo = Dir("repo");
        Write(Home, "roslynsense.json", """
            { "tools": { "razor": false } }
            """);
        Write(repo, "roslynsense.json", """
            { "tools": { "webForms": false } }
            """);

        var config = RoslynSenseConfigLoader.LoadLayers(repo).Config;

        Assert.NotNull(config);
        Assert.False(config.Tools.Razor);      // only the global file said so
        Assert.False(config.Tools.WebForms);   // only the repo file said so
        Assert.True(config.Tools.Proto);       // neither did
    }

    [Fact]
    public void ArraysReplaceRatherThanAppend()
    {
        string repo = Dir("repo");
        Write(Home, "roslynsense.json", """
            { "preload": ["global.sln"] }
            """);
        Write(repo, "roslynsense.json", """
            { "preload": ["repo.sln"] }
            """);

        var config = RoslynSenseConfigLoader.LoadLayers(repo).Config;

        Assert.Equal(["repo.sln"], config!.Preload);
    }

    [Fact]
    public void ConnectionsMergeByAlias()
    {
        string repo = Dir("repo");
        Write(Home, "roslynsense.json", """
            { "database": { "connections": { "shared": "psql:Host=localhost;Database=a" } } }
            """);
        Write(repo, "roslynsense.local.json", """
            { "database": { "connections": { "mine": "psql:Host=localhost;Database=b" } } }
            """);

        var connections = RoslynSenseConfigLoader.LoadLayers(repo).Config!.Database.Connections;

        Assert.Equal(2, connections.Count);
        Assert.Contains("shared", connections.Keys);
        Assert.Contains("mine", connections.Keys);
    }

    /// <summary>
    /// A layer that does not parse is reported and skipped. The alternative — no configuration at
    /// all because one file was mid-save — is strictly worse than the rest of the answer.
    /// </summary>
    [Fact]
    public void ABrokenLayerIsReportedAndTheOthersStillApply()
    {
        string repo = Dir("repo");
        Write(Home, "roslynsense.json", """
            { "tableFormat": "toon" }
            """);
        Write(repo, "roslynsense.json", "{ not json");

        var layered = RoslynSenseConfigLoader.LoadLayers(repo);

        Assert.NotNull(layered.LoadError);
        Assert.Contains("roslynsense.json", layered.LoadError);
        Assert.Equal("toon", layered.Config!.TableFormat);
    }

    [Fact]
    public void EveryCandidateIsListedWhetherOrNotItExists()
    {
        string repo = Dir("repo");
        Write(repo, "roslynsense.json", "{}");

        var layered = RoslynSenseConfigLoader.LoadLayers(repo);

        Assert.Contains(layered.Layers, layer => layer.Scope == ConfigScope.Global && !layer.Exists);
        Assert.Contains(layered.Layers, layer => layer.Scope == ConfigScope.RepoLocal && !layer.Exists);
        Assert.Contains(layered.Layers, layer => layer.Scope == ConfigScope.Personal && !layer.Exists);
        Assert.Contains(layered.Layers, layer => layer.Scope == ConfigScope.Repo && layer.Exists);
    }

    [Fact]
    public void TheNamedPathIsTheStrongestFileThatExists()
    {
        string repo = Dir("repo");
        Write(repo, "roslynsense.json", "{}");
        Write(repo, "roslynsense.local.json", "{}");

        var layered = RoslynSenseConfigLoader.LoadLayers(repo);

        Assert.EndsWith("roslynsense.local.json", layered.PrimaryPath);
    }
}

/// <summary>Where the personal layers live.</summary>
public sealed class ConfigPathsTests
{
    [Fact]
    public void MangledDirectoriesReadBackAsThePathTheyCameFrom()
    {
        string mangled = ConfigPaths.MangleDirectory(@"D:\Sources\RoslynSense");

        Assert.StartsWith("D--Sources-RoslynSense-", mangled);
    }

    [Fact]
    public void TrailingSeparatorsAndSlashDirectionDoNotChangeTheAnswer()
    {
        string plain = ConfigPaths.MangleDirectory(@"D:\Sources\RoslynSense");

        Assert.Equal(plain, ConfigPaths.MangleDirectory(@"D:\Sources\RoslynSense\"));
        Assert.Equal(plain, ConfigPaths.MangleDirectory("D:/Sources/RoslynSense"));
    }

    /// <summary>
    /// The readable half is lossy — these two paths flatten to the same text — so the hash is
    /// what actually keeps two checkouts from sharing one personal settings file.
    /// </summary>
    [Fact]
    public void DifferentPathsMangleDifferently()
    {
        Assert.NotEqual(
            ConfigPaths.MangleDirectory(@"D:\a\b-c"),
            ConfigPaths.MangleDirectory(@"D:\a-b\c"));
    }
}

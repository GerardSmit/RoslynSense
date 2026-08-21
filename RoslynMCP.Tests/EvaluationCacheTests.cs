using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The persistent evaluation cache: what round-trips through it, and what invalidates it.
/// </summary>
/// <remarks>
/// The round-trip test is load-bearing in a way unit tests usually are not. The cache serializes
/// Roslyn's <see cref="ProjectFileInfo"/> contract directly with System.Text.Json, and those are
/// someone else's records: a Roslyn update could change a property to a shape STJ silently
/// deserializes as empty, and nothing at the call site would fail — solutions would just load
/// wrong from cache. This is the tripwire.
/// </remarks>
public class EvaluationCacheTests : IDisposable
{
    private readonly string _projectDir;
    private readonly string _projectPath;

    private static readonly ImmutableDictionary<string, string> Properties =
        ImmutableDictionary<string, string>.Empty.Add("Configuration", "Debug");

    public EvaluationCacheTests()
    {
        _projectDir = Path.Combine(Path.GetTempPath(), "eval-cache-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectDir);
        _projectPath = Path.Combine(_projectDir, "App.csproj");
        File.WriteAllText(_projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(_projectDir, "Program.cs"), "class Program { }");
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectDir, recursive: true); } catch { }
    }

    private ProjectFileInfo MakeInfo(ImmutableArray<string> contentFilePaths = default) => new()
    {
        IsEmpty = false,
        Language = LanguageNames.CSharp,
        FilePath = _projectPath,
        OutputFilePath = Path.Combine(_projectDir, "bin", "App.dll"),
        OutputRefFilePath = Path.Combine(_projectDir, "obj", "ref", "App.dll"),
        IntermediateOutputFilePath = Path.Combine(_projectDir, "obj", "App.dll"),
        DefaultNamespace = "App",
        TargetFramework = "net10.0",
        TargetFrameworkIdentifier = ".NETCoreApp",
        TargetFrameworkVersion = "v10.0",
        CommandLineArgs = ["/nologo", "/define:DEBUG"],
        Documents =
        [
            new DocumentFileInfo(
                Path.Combine(_projectDir, "Program.cs"), "Program.cs",
                isLinked: false, isGenerated: false, folders: []),
        ],
        AdditionalDocuments = [],
        AnalyzerConfigDocuments = [],
        ProjectReferences =
        [
            new ProjectFileReference(@"..\Lib\Lib.csproj", aliases: ["global"], referenceOutputAssembly: true),
        ],
        ProjectCapabilities = ["CSharp", "TestContainer"],
        ContentFilePaths = contentFilePaths.IsDefault ? [] : contentFilePaths,
        PackageReferences = [new PackageReferenceItem("xunit", "2.9.0")],
        MetadataReferences = [new MetadataReferenceItem(@"C:\refs\System.Runtime.dll", [])],
        CodePage = 65001,
        ChecksumAlgorithm = "Sha256",
        FileGlobs = [new FileGlobs(Includes: ["**/*.cs"], Excludes: ["bin/**"], Removes: [])],
    };

    private async Task<(bool hit, ImmutableArray<ProjectFileInfo> infos, ImmutableArray<string> outputs)>
        StoreThenGetAsync(ImmutableDictionary<string, string> properties)
    {
        EvaluationCache.Store(_projectPath, Properties, [MakeInfo()], ["out1", "out2"]);
        await EvaluationCache.WhenStoresIdleAsync();

        bool hit = EvaluationCache.TryGet(_projectPath, properties, out var infos, out var outputs);
        return (hit, infos, outputs);
    }

    [Fact]
    public async Task RoundTripsEveryFieldThroughDisk()
    {
        var (hit, infos, outputs) = await StoreThenGetAsync(Properties);

        Assert.True(hit);

        // Assert.Equal<string> throughout, never the two-argument inference: with both sides
        // ImmutableArray<string> that resolves to Equal<T>(T, T), which for ImmutableArray is
        // reference equality and fails on identical contents.
        Assert.Equal<string>(["out1", "out2"], outputs);

        var info = Assert.Single(infos);
        var expected = MakeInfo();

        Assert.Equal(expected.Language, info.Language);
        Assert.Equal(expected.FilePath, info.FilePath);
        Assert.Equal(expected.OutputFilePath, info.OutputFilePath);
        Assert.Equal(expected.OutputRefFilePath, info.OutputRefFilePath);
        Assert.Equal(expected.IntermediateOutputFilePath, info.IntermediateOutputFilePath);
        Assert.Equal(expected.DefaultNamespace, info.DefaultNamespace);
        Assert.Equal(expected.TargetFramework, info.TargetFramework);
        Assert.Equal(expected.TargetFrameworkIdentifier, info.TargetFrameworkIdentifier);
        Assert.Equal(expected.TargetFrameworkVersion, info.TargetFrameworkVersion);
        Assert.Equal<string>(expected.CommandLineArgs, info.CommandLineArgs);
        Assert.Equal<string>(expected.ProjectCapabilities, info.ProjectCapabilities);
        Assert.Equal(expected.CodePage, info.CodePage);
        Assert.Equal(expected.ChecksumAlgorithm, info.ChecksumAlgorithm);

        // The nested contract types have no parameterless constructors, so these are where a
        // Roslyn shape change would silently produce empty objects. Check every member.
        var doc = Assert.Single(info.Documents);
        Assert.Equal(Path.Combine(_projectDir, "Program.cs"), doc.FilePath);
        Assert.Equal("Program.cs", doc.LogicalPath);
        Assert.False(doc.IsLinked);
        Assert.False(doc.IsGenerated);
        Assert.Empty(doc.Folders);

        var reference = Assert.Single(info.ProjectReferences);
        Assert.Equal(@"..\Lib\Lib.csproj", reference.Path);
        Assert.Equal<string>(["global"], reference.Aliases);
        Assert.True(reference.ReferenceOutputAssembly);

        var package = Assert.Single(info.PackageReferences);
        Assert.Equal("xunit", package.Name);
        Assert.Equal("2.9.0", package.VersionRange);

        var metadata = Assert.Single(info.MetadataReferences);
        Assert.Equal(@"C:\refs\System.Runtime.dll", metadata.Path);
        Assert.Empty(metadata.Aliases);

        var globs = Assert.Single(info.FileGlobs);
        Assert.Equal<string>(["**/*.cs"], globs.Includes);
        Assert.Equal<string>(["bin/**"], globs.Excludes);
        Assert.Empty(globs.Removes);
    }

    [Fact]
    public async Task MissesAfterTheProjectFileChanges()
    {
        var (hit, _, _) = await StoreThenGetAsync(Properties);
        Assert.True(hit);

        File.WriteAllText(_projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup /></Project>");

        Assert.False(EvaluationCache.TryGet(_projectPath, Properties, out _, out _));
    }

    [Fact]
    public async Task MissesAfterASourceFileAppears()
    {
        var (hit, _, _) = await StoreThenGetAsync(Properties);
        Assert.True(hit);

        // The project file did not change, but SDK globbing means evaluation would now include
        // one more document. The fingerprint has to see it.
        File.WriteAllText(Path.Combine(_projectDir, "New.cs"), "class New { }");

        Assert.False(EvaluationCache.TryGet(_projectPath, Properties, out _, out _));
    }

    [Fact]
    public async Task StillHitsAfterASourceFileIsMerelyEdited()
    {
        var (hit, _, _) = await StoreThenGetAsync(Properties);
        Assert.True(hit);

        // Editing contents changes no item list, and hashing every source would cost a real
        // slice of what the cache saves. Codified so a future "safer" fingerprint that hashes
        // contents has to argue with this test first.
        File.WriteAllText(Path.Combine(_projectDir, "Program.cs"), "class Program { int X; }");

        Assert.True(EvaluationCache.TryGet(_projectPath, Properties, out _, out _));
    }

    [Fact]
    public async Task MissesUnderDifferentGlobalProperties()
    {
        var release = Properties.SetItem("Configuration", "Release");

        var (hit, _, _) = await StoreThenGetAsync(release);
        Assert.False(hit);
    }

    [Fact]
    public async Task MissesAfterAnAncestorDirectoryBuildPropsChanges()
    {
        var (hit, _, _) = await StoreThenGetAsync(Properties);
        Assert.True(hit);

        File.WriteAllText(Path.Combine(_projectDir, "Directory.Build.props"),
            "<Project><PropertyGroup><LangVersion>latest</LangVersion></PropertyGroup></Project>");

        Assert.False(EvaluationCache.TryGet(_projectPath, Properties, out _, out _));
    }

    [Fact]
    public async Task MissesAfterTheRestoreGraphChanges()
    {
        var (hit, _, _) = await StoreThenGetAsync(Properties);
        Assert.True(hit);

        // The restore graph is stamped (size + timestamp) rather than content-hashed — see the
        // fingerprint — but a stamp appearing where none was is still a change.
        Directory.CreateDirectory(Path.Combine(_projectDir, "obj"));
        File.WriteAllText(Path.Combine(_projectDir, "obj", "project.assets.json"), "{}");

        Assert.False(EvaluationCache.TryGet(_projectPath, Properties, out _, out _));
    }

    [Fact]
    public async Task MissesAfterAFileOfARecordedExtensionAppears()
    {
        // The evaluation consumed a .json content file, so the entry records .json as watched —
        // the "exotic glob" case a fixed extension list cannot anticipate. A new .json appearing
        // must then miss, exactly as a new .cs would.
        File.WriteAllText(Path.Combine(_projectDir, "settings.json"), "{}");
        EvaluationCache.Store(_projectPath, Properties,
            [MakeInfo([Path.Combine(_projectDir, "settings.json")])], ["out1"]);
        await EvaluationCache.WhenStoresIdleAsync();

        Assert.True(EvaluationCache.TryGet(_projectPath, Properties, out _, out _));

        File.WriteAllText(Path.Combine(_projectDir, "other.json"), "{}");

        Assert.False(EvaluationCache.TryGet(_projectPath, Properties, out _, out _));
    }

    [Fact]
    public async Task IgnoresFilesOfExtensionsTheProjectNeverEvaluated()
    {
        var (hit, _, _) = await StoreThenGetAsync(Properties);
        Assert.True(hit);

        // No .json ever reached this project's evaluation, so its entry does not watch them —
        // per-entry recording is what keeps every other project from paying for one project's
        // exotic globs.
        File.WriteAllText(Path.Combine(_projectDir, "notes.json"), "{}");

        Assert.True(EvaluationCache.TryGet(_projectPath, Properties, out _, out _));
    }

    [Fact]
    public async Task WatchesExtensionsConfiguredByEnvironment()
    {
        // For evaluation shapes derivation cannot see (an import reading files by convention),
        // ROSLYNMCP_EVAL_CACHE_EXTENSIONS widens the watched set for every project.
        Environment.SetEnvironmentVariable("ROSLYNMCP_EVAL_CACHE_EXTENSIONS", "json");
        try
        {
            var (hit, _, _) = await StoreThenGetAsync(Properties);
            Assert.True(hit);

            File.WriteAllText(Path.Combine(_projectDir, "data.json"), "{}");

            Assert.False(EvaluationCache.TryGet(_projectPath, Properties, out _, out _));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ROSLYNMCP_EVAL_CACHE_EXTENSIONS", null);
        }
    }
}

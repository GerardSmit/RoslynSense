using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The pre-load source-generator build: a <c>ProjectReference</c> with
/// <c>OutputItemType="Analyzer"</c> whose output DLL does not exist yet is found by the XML scan
/// and built before the workspace loads its consumer. Without it, a fresh clone of any solution
/// that ships its own generator (DNN Platform's <c>[DnnDeprecated]</c> generator is the reported
/// case) opens with the generator silently absent and phantom compile errors on valid code.
/// </summary>
public class GeneratorBuildServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _consumerProject;
    private readonly string _generatorProject;

    public GeneratorBuildServiceTests()
    {
        // A copy without bin/obj is exactly the fresh-clone state under test.
        _tempDir = Path.Combine(Path.GetTempPath(), $"GeneratorBuildTest_{Guid.NewGuid():N}");
        CopyDirectory(FixturePaths.SourceGenFixtureDir, _tempDir);
        _consumerProject = Path.Combine(_tempDir, "Consumer", "Consumer.csproj");
        _generatorProject = Path.Combine(_tempDir, "Generator", "Generator.csproj");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best effort */ }
    }

    [Fact]
    public void WhenGeneratorNeverBuiltThenScanReportsIt()
    {
        var unbuilt = GeneratorBuildService.FindUnbuiltGeneratorProjects(_consumerProject);

        Assert.Contains(_generatorProject, unbuilt, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void WhenGeneratorOutputExistsThenScanReportsNothing()
    {
        string outputDir = Path.Combine(_tempDir, "Generator", "bin", "Debug", "netstandard2.0");
        Directory.CreateDirectory(outputDir);
        File.WriteAllBytes(Path.Combine(outputDir, "Generator.dll"), [1, 2, 3, 4]);

        var unbuilt = GeneratorBuildService.FindUnbuiltGeneratorProjects(_consumerProject);

        Assert.Empty(unbuilt);
    }

    [Fact]
    public void WhenReferenceIsNotAnAnalyzerThenScanReportsNothing()
    {
        // Same missing bin, but an ordinary ProjectReference: building it is the build system's
        // job, not the load's — the workspace compiles referenced projects from source.
        string consumerPath = Path.Combine(_tempDir, "Consumer", "Consumer.csproj");
        string content = File.ReadAllText(consumerPath);
        File.WriteAllText(consumerPath, content
            .Replace("OutputItemType=\"Analyzer\"", "")
            .Replace("ReferenceOutputAssembly=\"false\"", ""));

        var unbuilt = GeneratorBuildService.FindUnbuiltGeneratorProjects(consumerPath);

        Assert.Empty(unbuilt);
    }

    /// <summary>
    /// A generator built to a custom <c>OutputPath</c> is built, just not under <c>bin\</c> where
    /// the fast probe looks — so the probe misses, and the load used to rebuild it on every fresh
    /// daemon. Rebuilding is not merely wasted seconds: the DLL write it produces is a rebuild
    /// event to every workspace pinning that directory, which is what turned "open the solution"
    /// into "reload the solution". MSBuild is asked where the output really goes before any build
    /// is paid for.
    /// </summary>
    [Fact]
    public async Task WhenGeneratorIsBuiltToACustomOutputPathThenEnsureDoesNotRebuild()
    {
        File.WriteAllText(_generatorProject, File.ReadAllText(_generatorProject).Replace(
            "<PropertyGroup>",
            "<PropertyGroup>\n    <OutputPath>out\\</OutputPath>"));

        // Where MSBuild's TargetPath points for this project: OutputPath plus the appended TFM.
        string dll = Path.Combine(_tempDir, "Generator", "out", "netstandard2.0", "Generator.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
        File.WriteAllBytes(dll, [1, 2, 3, 4]);
        var stamp = File.GetLastWriteTimeUtc(dll);

        await GeneratorBuildService.EnsureGeneratorsBuiltAsync(_consumerProject, CancellationToken.None);

        // Untouched: a build would have overwritten the file at its own TargetPath.
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(dll));
        Assert.False(
            Directory.Exists(Path.Combine(_tempDir, "Generator", "bin")),
            "a build ran despite the generator's output existing at its TargetPath");
    }

    [Fact]
    public async Task WhenGeneratorNeverBuiltThenEnsureBuildsItsOutput()
    {
        await GeneratorBuildService.EnsureGeneratorsBuiltAsync(_consumerProject, CancellationToken.None);

        string binDir = Path.Combine(_tempDir, "Generator", "bin");
        Assert.True(
            Directory.Exists(binDir)
            && Directory.EnumerateFiles(binDir, "Generator.dll", SearchOption.AllDirectories).Any(),
            $"Expected the pre-load build to produce Generator.dll under '{binDir}'.");
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            string dirName = Path.GetFileName(dir);
            if (dirName is "obj" or "bin") continue;
            CopyDirectory(dir, Path.Combine(destDir, dirName));
        }
    }
}

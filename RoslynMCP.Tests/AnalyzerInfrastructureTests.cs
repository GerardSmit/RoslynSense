using Microsoft.CodeAnalysis.Diagnostics;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

public class AnalyzerInfrastructureTests
{
    [Fact]
    public async Task WhenAnalyzersEnabledThenProjectExposesAnalyzerReferences()
    {
        var project = await RoslynTestHelpers.OpenProjectAsync(
            FixturePaths.SampleProjectFile,
            FixturePaths.WarningsFile);

        var analyzerPaths = AnalyzerService.DiscoverAnalyzerPathsFromProject(project);

        Assert.NotEmpty(analyzerPaths);
        Assert.All(analyzerPaths, path => Assert.True(File.Exists(path), path));
    }

    [Fact]
    public async Task WhenHostLoadsAnalyzersThenEntriesCanBeCachedAndEvicted()
    {
        var project = await RoslynTestHelpers.OpenProjectAsync(
            FixturePaths.SampleProjectFile,
            FixturePaths.WarningsFile);
        var analyzerPaths = AnalyzerService.DiscoverAnalyzerPathsFromProject(project);

        using var host = new AnalyzerHost();

        Assert.Empty(host.GetOrLoadAnalyzers(project.FilePath!, Array.Empty<string>()));

        var first = host.GetOrLoadAnalyzers(project.FilePath!, analyzerPaths);
        var second = host.GetOrLoadAnalyzers(project.FilePath!, analyzerPaths);

        Assert.NotEmpty(first);
        Assert.Equal(first.Length, second.Length);

        host.EvictForProject(project.FilePath!);

        var third = host.GetOrLoadAnalyzers(project.FilePath!, analyzerPaths);

        Assert.NotEmpty(third);

        host.UnloadAll();
    }

    [Fact]
    public async Task WhenAnalyzerAssemblyLoadedInCustomContextThenTypesCanBeEnumerated()
    {
        var project = await RoslynTestHelpers.OpenProjectAsync(
            FixturePaths.SampleProjectFile,
            FixturePaths.WarningsFile);
        string analyzerPath = AnalyzerService.DiscoverAnalyzerPathsFromProject(project).First();

        var loadContext = new AnalyzerLoadContext(analyzerPath);
        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(analyzerPath);
            Assert.NotNull(assembly);
            Assert.Contains("Analyzer", assembly.GetName().Name, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            loadContext.Unload();
        }
    }

    [Fact]
    public async Task WhenRunningAnalyzersOnWarningFileThenAnalyzerDiagnosticsAreReturned()
    {
        var project = await RoslynTestHelpers.OpenProjectAsync(
            FixturePaths.SampleProjectFile,
            FixturePaths.WarningsFile);
        var compilation = await project.GetCompilationAsync();
        Assert.NotNull(compilation);

        var writer = new StringWriter();
        var diagnostics = (await AnalyzerService.RunAnalyzersAsync(
                project,
                compilation!,
                FixturePaths.WarningsFile,
                writer))
            .ToList();

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id.StartsWith("CA", StringComparison.Ordinal));
        Assert.Contains("Found", writer.ToString());
    }

    [Fact]
    public async Task WhenProjectHasNoAnalyzerReferencesThenAnalyzerServiceReturnsEmptyDiagnostics()
    {
        var project = await RoslynTestHelpers.OpenProjectAsync(
            FixturePaths.BrokenProjectFile,
            FixturePaths.BrokenSemanticFile);
        var analyzerFreeSolution = project.Solution;
        foreach (var analyzerReference in project.AnalyzerReferences)
            analyzerFreeSolution = analyzerFreeSolution.RemoveAnalyzerReference(project.Id, analyzerReference);

        project = analyzerFreeSolution.GetProject(project.Id)!;

        var compilation = await project.GetCompilationAsync();
        Assert.NotNull(compilation);

        var writer = new StringWriter();
        var diagnostics = (await AnalyzerService.RunAnalyzersAsync(
                project,
                compilation!,
                FixturePaths.BrokenSemanticFile,
                writer))
            .ToList();

        Assert.Empty(diagnostics);
        Assert.Contains("No analyzer references found", writer.ToString());
    }

    [Fact]
    public void WhenAnalyzerHostDisposedTwiceThenDoesNotThrow()
    {
        var host = new AnalyzerHost();
        host.Dispose();
        host.Dispose(); // Second dispose should be a no-op
    }

    [Fact]
    public void WhenAnalyzerHostDisposedThenGetOrLoadThrowsObjectDisposed()
    {
        var host = new AnalyzerHost();
        host.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => host.GetOrLoadAnalyzers("test", ["dummy.dll"]));
    }

    [Fact]
    public async Task WhenUnloadAllCalledMultipleTimesThenIdempotent()
    {
        var project = await RoslynTestHelpers.OpenProjectAsync(
            FixturePaths.SampleProjectFile,
            FixturePaths.WarningsFile);
        var analyzerPaths = AnalyzerService.DiscoverAnalyzerPathsFromProject(project);

        using var host = new AnalyzerHost();
        host.GetOrLoadAnalyzers(project.FilePath!, analyzerPaths);

        host.UnloadAll();
        host.UnloadAll(); // Should not throw
    }

    [Fact]
    public async Task WhenEvictForNonExistentProjectThenNoException()
    {
        using var host = new AnalyzerHost();

        // Evicting a non-existent project should not throw
        host.EvictForProject("nonexistent-project");
    }

    [Fact]
    public void WhenAnalyzerShadowCopiedThenTryGetSourceDirectoryMapsBothPathsToSource()
    {
        // Regression: AnalyzerHost keyed its rebuild-eviction map via NeedsShadowCopy +
        // Path.GetDirectoryName(path). After the workspace rebind, analyzer paths are shadow
        // paths, so NeedsShadowCopy is false and the dir is a temp dir — the auto-evict map
        // never populated and rebuild events never matched. TryGetSourceDirectory fixes this
        // by mapping either the original or the shadow path back to the watched source dir.
        string sourceDir = Path.Combine(
            Path.GetTempPath(), "rmcp-shadow-map-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceDir);
        try
        {
            string originalDll = Path.Combine(sourceDir, "Fake.Analyzer.dll");
            File.Copy(typeof(AnalyzerInfrastructureTests).Assembly.Location, originalDll);

            using var shadow = new ShadowCopyManager();

            string shadowPath = shadow.GetLoadPath(originalDll);

            // Sanity: a project-output DLL is shadow-copied to a different location...
            Assert.NotEqual(
                Path.GetFullPath(originalDll), Path.GetFullPath(shadowPath),
                StringComparer.OrdinalIgnoreCase);
            // ...and the shadow path itself reports NeedsShadowCopy=false — the old bug trigger.
            Assert.False(shadow.NeedsShadowCopy(shadowPath));

            string expected = Path.GetFullPath(sourceDir);
            Assert.Equal(expected, shadow.TryGetSourceDirectory(originalDll), StringComparer.OrdinalIgnoreCase);
            Assert.Equal(expected, shadow.TryGetSourceDirectory(shadowPath), StringComparer.OrdinalIgnoreCase);

            // A path under a never-shadowed directory maps to nothing.
            Assert.Null(shadow.TryGetSourceDirectory(
                Path.Combine(Path.GetTempPath(), "rmcp-unwatched", "x.dll")));
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }
}

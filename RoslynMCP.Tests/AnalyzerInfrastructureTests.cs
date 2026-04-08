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
}

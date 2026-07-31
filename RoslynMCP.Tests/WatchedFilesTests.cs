using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>workspace/didChangeWatchedFiles: external changes must reach the loaded workspace
/// without a manual reload, and a burst must not reload once per event.</summary>
public class WatchedFilesTests
{
    [Fact]
    public async Task SourceFileCreatedOnDiskBecomesResolvableWithoutManualReload()
    {
        // Load the project so a stale snapshot exists to invalidate.
        await RoslynTestHelpers.OpenProjectAsync(
            FixturePaths.SampleProjectFile, FixturePaths.CalculatorFile);

        string newFile = Path.Combine(FixturePaths.SampleProjectDir, $"WatchedAdded{Guid.NewGuid():N}.cs");
        await File.WriteAllTextAsync(newFile, """
            namespace SampleProject;

            public sealed class WatchedAddedType
            {
                public int Answer() => 42;
            }
            """);
        try
        {
            Assert.Null(await LspDocumentResolver.ResolveAsync(newFile, default));

            var outcome = await WatchedFilesHandler.ProcessAsync(
                [new FileEvent(LspConverters.PathToUri(newFile), FileChangeType.Created)], default);

            Assert.False(outcome.ReloadedWorkspace);
            Assert.Contains(FixturePaths.SampleProjectFile, outcome.EvictedProjects, StringComparer.OrdinalIgnoreCase);
            Assert.NotNull(await LspDocumentResolver.ResolveAsync(newFile, default));
        }
        finally
        {
            File.Delete(newFile);
            await WorkspaceService.EvictAllAsync();
        }
    }

    [Fact]
    public async Task ProjectFileChangeReloadsTheWholeWorkspace()
    {
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);
        Assert.True(WorkspaceService.CachedEntryCount > 0);

        var outcome = await WatchedFilesHandler.ProcessAsync(
            [new FileEvent(LspConverters.PathToUri(FixturePaths.SampleProjectFile), FileChangeType.Changed)],
            default);

        Assert.True(outcome.ReloadedWorkspace);
        Assert.Equal(0, WorkspaceService.CachedEntryCount);
    }

    [Fact]
    public async Task EditorConfigChangeReloadsAndClearsAnalyzerCache()
    {
        var (_, document) = await RoslynTestHelpers.OpenDocumentAsync(FixturePaths.WarningsFile);
        await AnalyzerDiagnosticCache.GetOrComputeAsync(document, default);
        string? version = await AnalyzerDiagnosticCache.GetVersionAsync(document, default);
        Assert.True(AnalyzerDiagnosticCache.IsComputed(document, version));

        string editorConfig = Path.Combine(FixturePaths.SampleProjectDir, ".editorconfig");
        var outcome = await WatchedFilesHandler.ProcessAsync(
            [new FileEvent(LspConverters.PathToUri(editorConfig), FileChangeType.Changed)], default);

        // Severities live in the loaded project's analyzer options, and every cached result was
        // computed under the previous rules.
        Assert.True(outcome.ReloadedWorkspace);
        Assert.False(AnalyzerDiagnosticCache.IsComputed(document, version));
    }

    [Fact]
    public async Task BuildOutputEventsAreIgnored()
    {
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);
        int before = WorkspaceService.CachedEntryCount;

        var outcome = await WatchedFilesHandler.ProcessAsync(
        [
            new FileEvent(LspConverters.PathToUri(
                Path.Combine(FixturePaths.SampleProjectDir, "obj", "Debug", "Generated.cs")),
                FileChangeType.Created),
            new FileEvent(LspConverters.PathToUri(
                Path.Combine(FixturePaths.SampleProjectDir, "bin", "Debug", "App.csproj")),
                FileChangeType.Changed),
        ], default);

        Assert.False(outcome.DidAnything);
        Assert.Equal(before, WorkspaceService.CachedEntryCount);
    }

    [Fact]
    public async Task BurstOfSourceEventsInOneProjectEvictsItOnce()
    {
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);

        var events = Enumerable.Range(0, 50)
            .Select(i => new FileEvent(
                LspConverters.PathToUri(Path.Combine(FixturePaths.SampleProjectDir, $"Burst{i}.cs")),
                FileChangeType.Created))
            .ToArray();

        var outcome = await WatchedFilesHandler.ProcessAsync(events, default);

        // 50 events, one directory: each project there is evicted once, not once per event.
        Assert.Equal(
            WatchedFilesHandler.FindNearestProjectFiles(
                Path.Combine(FixturePaths.SampleProjectDir, "Burst0.cs")).Count,
            outcome.EvictedProjects.Count);
    }

    [Fact]
    public void NearestProjectFilesWalkUpAndReturnEveryCandidate()
    {
        string nested = Path.Combine(FixturePaths.SampleProjectDir, "Models", "Result.cs");

        var projects = WatchedFilesHandler.FindNearestProjectFiles(nested);

        // The fixture folder holds two projects; picking one arbitrarily would evict the wrong
        // snapshot and leave the stale one answering requests.
        Assert.Contains(FixturePaths.SampleProjectFile, projects, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(FixturePaths.AlternateProjectFile, projects, StringComparer.OrdinalIgnoreCase);

        Assert.Empty(WatchedFilesHandler.FindNearestProjectFiles(
            Path.Combine(Path.GetTempPath(), $"no-project-{Guid.NewGuid():N}", "Orphan.cs")));
    }
}

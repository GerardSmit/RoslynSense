using RoslynMCP.Config;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The eager solution load behind Search Everywhere: with it, the whole solution is searchable
/// from the moment the server is up; without it, only the projects some open file dragged in.
/// </summary>
[Collection(SharedState.Name)]
public class SolutionWarmupTests
{
    /// <summary>
    /// ProjectB references ProjectA and nothing references ProjectB, so ProjectB is exactly what
    /// demand-driven loading never reaches on its own: opening a file in ProjectA pulls in its
    /// closure, which does not include its consumer. Finding <c>Caller</c> with no document open
    /// at all is therefore the whole feature in one assertion.
    /// </summary>
    [Fact]
    public async Task EveryProjectIsSearchableWithoutOpeningAFile()
    {
        string? previous = WorkspaceService.BoundSolutionPath;
        bool previousSetting = LspFeatureOptions.LoadEntireSolution;

        try
        {
            await WorkspaceService.EvictAllAsync();
            LspFeatureOptions.LoadEntireSolution = true;
            SolutionWarmup.Reset();
            WorkspaceService.BindSolution(FixturePaths.MultiSolutionFile);

            await SolutionWarmup.Start();

            var hits = await SearchEverywhereHandler.SearchAsync(
                new SearchEverywhereParams("Caller"), default);

            Assert.Contains(hits.Items, item =>
                item.Name == "Caller"
                && item.Path.Contains("ProjectB", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            LspFeatureOptions.LoadEntireSolution = previousSetting;
            SolutionWarmup.Reset();
            WorkspaceService.BindSolution(previous);
        }
    }

    /// <summary>
    /// The caches a search reads are built before a search asks for them.
    /// </summary>
    /// <remarks>
    /// Asserted as "every project already holds its compilation" rather than as a stopwatch,
    /// because the thing being pinned is that the work happened, not how long it took. On a real
    /// solution the difference is a first query of seven seconds against one of a fifth of a
    /// second — and since the panel cancels its request on every keystroke, seconds per search is
    /// seconds per character, which a typist outruns until the search appears to find nothing.
    /// </remarks>
    [Fact]
    public async Task TheSymbolCachesAreBuiltBeforeAnyoneSearches()
    {
        string? previous = WorkspaceService.BoundSolutionPath;
        bool previousSetting = LspFeatureOptions.LoadEntireSolution;

        try
        {
            await WorkspaceService.EvictAllAsync();
            LspFeatureOptions.LoadEntireSolution = true;
            SolutionWarmup.Reset();
            WorkspaceService.BindSolution(FixturePaths.MultiSolutionFile);

            await SolutionWarmup.Start();
            await SolutionWarmup.WarmedSymbols;

            var solution = WorkspaceService.TryGetMostRecentSolution();
            Assert.NotNull(solution);
            Assert.NotEmpty(solution!.Projects);

            // TryGetCompilation, not GetCompilationAsync: the second would build the thing it is
            // supposed to be checking for and pass however cold the workspace was.
            Assert.All(solution.Projects, project =>
                Assert.True(
                    project.TryGetCompilation(out _),
                    $"'{project.Name}' had no compilation after the warm pass"));
        }
        finally
        {
            LspFeatureOptions.LoadEntireSolution = previousSetting;
            SolutionWarmup.Reset();
            WorkspaceService.BindSolution(previous);
        }
    }

    /// <summary>
    /// The Solution Explorer draws the same fact the search box depends on: a project the
    /// workspace has not loaded answers nothing, and a row that looks identical to a loaded one
    /// makes that indistinguishable from a project with nothing in it.
    /// </summary>
    [Fact]
    public async Task ProjectsTheWorkspaceHasNotLoadedAreMarked()
    {
        string? previous = WorkspaceService.BoundSolutionPath;

        try
        {
            await WorkspaceService.EvictAllAsync();
            SolutionWarmup.Reset();
            WorkspaceService.BindSolution(FixturePaths.MultiSolutionFile);

            var projects = await SolutionTreeHandler.ChildrenAsync(
                new SolutionTreeParams(NodeId: $"solution:{FixturePaths.MultiSolutionFile}"), default);

            Assert.NotEmpty(projects);
            Assert.All(projects, node =>
            {
                Assert.Equal("not loaded", node.Description);
                Assert.True(node.Dimmed);

                // Still expandable and still runnable-or-not on its own merits: not loaded yet is
                // not the user's Unload Project, and must not take the row's actions away.
                Assert.True(node.HasChildren);
                Assert.NotEqual(SolutionNodeKind.UnloadedProject, node.ContextValue);
            });
        }
        finally
        {
            WorkspaceService.BindSolution(previous);
        }
    }

    /// <summary>
    /// Off means off: nothing is loaded, and the solution-wide requests are as narrow as they were
    /// before this existed. The setting is what a solution too large to hold at once is left with.
    /// </summary>
    [Fact]
    public async Task TurningItOffLoadsNothing()
    {
        string? previous = WorkspaceService.BoundSolutionPath;
        bool previousSetting = LspFeatureOptions.LoadEntireSolution;

        try
        {
            await WorkspaceService.EvictAllAsync();
            LspFeatureOptions.LoadEntireSolution = false;
            SolutionWarmup.Reset();
            WorkspaceService.BindSolution(FixturePaths.MultiSolutionFile);

            await SolutionWarmup.Start();

            // And the wait a search does must not block on a load that was never started.
            await SolutionWarmup.WaitAsync(default);

            Assert.Null(WorkspaceService.TryGetMostRecentSolution());
        }
        finally
        {
            LspFeatureOptions.LoadEntireSolution = previousSetting;
            SolutionWarmup.Reset();
            WorkspaceService.BindSolution(previous);
        }
    }
}

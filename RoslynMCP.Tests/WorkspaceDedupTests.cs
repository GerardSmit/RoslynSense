using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

public class WorkspaceDedupTests
{
    [Fact]
    public async Task WhenTwoProjectsInSameSolutionOpenedThenWorkspaceIsShared()
    {
        // Regression: the cache used to key per .csproj, so opening sibling projects of one
        // solution spun up a full transitive workspace each (O(N^2) memory). Now a solution is
        // opened once and every member project is served from that single workspace.
        await WorkspaceService.EvictProjectForTests(FixturePaths.MultiProjectAFile);
        await WorkspaceService.EvictProjectForTests(FixturePaths.MultiProjectBFile);

        var (workspaceA, projectA) = await WorkspaceService.GetOrOpenProjectAsync(FixturePaths.MultiProjectAFile);
        var (workspaceB, projectB) = await WorkspaceService.GetOrOpenProjectAsync(FixturePaths.MultiProjectBFile);

        Assert.Same(workspaceA, workspaceB);
        Assert.Equal("ProjectA", projectA.Name);
        Assert.Equal("ProjectB", projectB.Name);

        // Both project paths resolve to a live cached entry (the shared solution workspace).
        Assert.True(WorkspaceService.IsProjectCachedForTests(FixturePaths.MultiProjectAFile));
        Assert.True(WorkspaceService.IsProjectCachedForTests(FixturePaths.MultiProjectBFile));
    }

    [Fact]
    public async Task WhenOneProjectOpenedThenSolutionIsNotFullyLoaded()
    {
        await WorkspaceService.EvictProjectForTests(FixturePaths.MultiProjectAFile);
        await WorkspaceService.EvictProjectForTests(FixturePaths.MultiProjectBFile);

        // ProjectA has no project references; ProjectB references ProjectA. Opening ProjectA must
        // load ONLY ProjectA — not the whole solution — so a 1000-project sln costs one project,
        // not a thousand, when a tool touches a single file.
        var (_, projectA) = await WorkspaceService.GetOrOpenProjectAsync(FixturePaths.MultiProjectAFile);
        Assert.Equal("ProjectA", projectA.Name);
        Assert.Equal(1, WorkspaceService.LoadedProjectCountForTests(FixturePaths.MultiProjectAFile));
        Assert.False(WorkspaceService.IsProjectCachedForTests(FixturePaths.MultiProjectBFile));

        // Opening ProjectB adds it incrementally to the SAME workspace, pulling its reference
        // ProjectA (already loaded → reused). Two projects now, one shared workspace.
        var (workspaceB, projectB) = await WorkspaceService.GetOrOpenProjectAsync(FixturePaths.MultiProjectBFile);
        var (workspaceA, _) = await WorkspaceService.GetOrOpenProjectAsync(FixturePaths.MultiProjectAFile);
        Assert.Equal("ProjectB", projectB.Name);
        Assert.Same(workspaceA, workspaceB);
        Assert.Equal(2, WorkspaceService.LoadedProjectCountForTests(FixturePaths.MultiProjectAFile));
    }

    [Fact]
    public async Task WhenSharedWorkspaceEvictedThenAllMemberProjectsAreUncached()
    {
        await WorkspaceService.GetOrOpenProjectAsync(FixturePaths.MultiProjectAFile);

        // Evicting via one member project must clear the reverse-index for every member,
        // since they all pointed at the same cache entry.
        await WorkspaceService.EvictProjectForTests(FixturePaths.MultiProjectAFile);

        Assert.False(WorkspaceService.IsProjectCachedForTests(FixturePaths.MultiProjectAFile));
        Assert.False(WorkspaceService.IsProjectCachedForTests(FixturePaths.MultiProjectBFile));
    }
}

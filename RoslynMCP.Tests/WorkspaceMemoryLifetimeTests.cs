using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services;
using RoslynMCP.Services.HotReload;
using RoslynMCP.Services.Memory;
using Xunit;

namespace RoslynMCP.Tests;

[Collection(SharedState.Name)]
public class WorkspaceMemoryLifetimeTests
{
    [Fact]
    public async Task OpenBufferProtectsWorkspaceUntilClosed()
    {
        var path = FixturePaths.SampleProjectFile;
        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(path);
        var document = project.Documents.First(d => d.FilePath is not null);
        OpenDocumentStore.Open("memory-owner", document.FilePath!, await document.GetTextAsync(), 1);
        try
        {
            WorkspaceService.AgeProjectForMemoryTests(path);
            WorkspaceService.SweepIdleForMemoryTests();
            Assert.True(WorkspaceService.IsProjectCachedForTests(path));
        }
        finally { OpenDocumentStore.Close("memory-owner", document.FilePath!); }
        WorkspaceService.SweepIdleForMemoryTests();
        Assert.False(WorkspaceService.IsProjectCachedForTests(path));
    }

    [Fact]
    public async Task RunningRequestProtectsUnownedWorkspaceUntilItCompletes()
    {
        var path = FixturePaths.SampleProjectFile;
        await WorkspaceService.GetOrOpenProjectAsync(path);
        WorkspaceService.AgeProjectForMemoryTests(path);
        using (HostMemoryTelemetry.Operation("test-navigation"))
        {
            WorkspaceService.SweepIdleForMemoryTests();
            Assert.True(WorkspaceService.IsProjectCachedForTests(path));
        }
        WorkspaceService.SweepIdleForMemoryTests();
        Assert.False(WorkspaceService.IsProjectCachedForTests(path));
    }

    [Fact]
    public async Task ActiveBaselineProtectsWorkspaceUntilLastOwnerReleases()
    {
        var path = FixturePaths.SampleProjectFile;
        var (session, _) = await HotReloadService.StartAsync(path, ownerId: "memory-baseline");
        Assert.NotNull(session);
        try
        {
            WorkspaceService.AgeProjectForMemoryTests(path);
            WorkspaceService.SweepIdleForMemoryTests();
            Assert.True(WorkspaceService.IsProjectCachedForTests(path));
        }
        finally { await session.StopAsync(); }
        WorkspaceService.SweepIdleForMemoryTests();
        Assert.False(WorkspaceService.IsProjectCachedForTests(path));
    }
}

using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The editor is told when the loaded project set moves underneath it.
/// </summary>
/// <remarks>
/// <para>
/// Everything the client caches — reference counts in the gutter, cross-project diagnostics, inlay
/// hints — is derived from whatever was loaded when it asked. The set grows whenever a search
/// widens the workspace, and vanishes when a workspace is thrown away, and in neither case did the
/// client have any way to find out.
/// </para>
/// <para>
/// Reported as: "the solution loads, but none of the references are showing. Code lens doesn't
/// work. Go to reference does work. And after I did go to reference, close ASCX, re-open, the
/// references loaded." Find-references widens the solution as part of what it does; the lens on
/// the same line was computed before that and was never asked again, so closing and reopening the
/// file was the user performing the invalidation by hand.
/// </para>
/// </remarks>
[Collection(SharedState.Name)]
public class WorkspaceInvalidationTests : IDisposable
{
    private readonly Action? _previous = WorkspaceService.ProjectSetChanged;

    public void Dispose() => WorkspaceService.ProjectSetChanged = _previous;

    [Fact]
    public async Task LoadingAProjectTellsTheEditorItsCachedAnswersAreStale()
    {
        await WorkspaceService.EvictAllAsync();

        int signals = 0;
        WorkspaceService.ProjectSetChanged = () => Interlocked.Increment(ref signals);

        await WorkspaceService.GetOrOpenProjectAsync(FixturePaths.SampleProjectFile);

        Assert.True(signals > 0,
            "loading a project must invalidate what the client cached from before it existed");
    }

    [Fact]
    public async Task ThrowingAWorkspaceAwayTellsTheEditorToo()
    {
        await WorkspaceService.GetOrOpenProjectAsync(FixturePaths.SampleProjectFile);

        int signals = 0;
        WorkspaceService.ProjectSetChanged = () => Interlocked.Increment(ref signals);

        await WorkspaceService.EvictAllAsync();

        Assert.True(signals > 0,
            "an evicted workspace leaves every cached answer wrong, and only the server knows");
    }

    /// <summary>
    /// A missing subscriber is not an error — the MCP-only host has no editor to tell.
    /// </summary>
    [Fact]
    public async Task ALoadSucceedsWithNobodyListening()
    {
        await WorkspaceService.EvictAllAsync();
        WorkspaceService.ProjectSetChanged = null;

        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(FixturePaths.SampleProjectFile);

        Assert.NotNull(project);
    }

    /// <summary>
    /// A subscriber that throws must not fail the load it was told about.
    /// </summary>
    [Fact]
    public async Task ASubscriberThatThrowsDoesNotBreakTheLoad()
    {
        await WorkspaceService.EvictAllAsync();
        WorkspaceService.ProjectSetChanged = () => throw new InvalidOperationException("client gone");

        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(FixturePaths.SampleProjectFile);

        Assert.NotNull(project);
    }
}

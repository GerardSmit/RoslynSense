using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The end of "everything is red until I restart the server": a restore that lands outside this
/// process has to reach the workspaces that were loaded before it.
/// </summary>
/// <remarks>
/// A build in a terminal, a <c>dotnet restore</c>, a package added from another editor and a branch
/// switch all change what a project resolves against, and none of them touches anything else this
/// process watches — the project file's timestamp does not move, and no source file changed. Without
/// the watcher the workspace kept serving a compilation resolved against the old graph, or against
/// no graph at all.
/// </remarks>
[Collection(SharedState.Name)]
public class RestoreWatcherTests
{
    private static string AssetsFileFor(string projectPath) =>
        Path.Combine(Path.GetDirectoryName(projectPath)!, "obj", "project.assets.json");

    [Fact]
    public async Task ARestoreOutsideTheProcessEvictsTheProjectLoadedBeforeIt()
    {
        string project = FixturePaths.SampleProjectFile;
        string assets = AssetsFileFor(project);

        Assert.True(
            File.Exists(assets),
            $"The fixture has to be restored for this test to mean anything; '{assets}' is missing.");

        byte[] original = await File.ReadAllBytesAsync(assets);
        try
        {
            await WorkspaceService.EvictAllAsync();
            RestoreWatcher.ResetForTests();
            using var armed = RestoreWatcher.ArmForTests();

            // Loading is what installs the watcher, on the code path every real load takes.
            await WorkspaceService.GetOrOpenProjectAsync(project);
            Assert.Empty(await WorkspaceService.ProjectsNotYetLoadedAsync([project]));

            // The install happens on a pool thread, and the fingerprint it records has to be taken
            // before the file below changes — otherwise the test races the thing it is testing.
            Assert.True(
                await WaitForAsync(() => RestoreWatcher.WatchedDirectoryCount > 0, TimeSpan.FromSeconds(10)),
                "The load did not install a restore watcher.");

            // What a restore looks like from the outside. The content has to actually differ:
            // byte-identical rewrites are ignored on purpose, which the next test covers.
            await File.WriteAllBytesAsync(assets, [.. original, .. " "u8]);

            Assert.True(
                await WaitForAsync(
                    async () => (await WorkspaceService.ProjectsNotYetLoadedAsync([project])).Count > 0,
                    TimeSpan.FromSeconds(20)),
                "The project was still loaded 20s after its assets file changed; it was never evicted.");
        }
        finally
        {
            RestoreWatcher.ResetForTests();
            await File.WriteAllBytesAsync(assets, original);
            await WorkspaceService.EvictAllAsync();
        }
    }

    /// <summary>
    /// The counterpart, and the reason the fingerprint is a content hash: a no-op restore rewrites
    /// the assets file, and treating that as a change would evict — and reload — every project in
    /// the solution every time anybody built anything.
    /// </summary>
    [Fact]
    public async Task AByteIdenticalRewriteLeavesTheWorkspaceAlone()
    {
        string project = FixturePaths.SampleProjectFile;
        string assets = AssetsFileFor(project);

        Assert.True(File.Exists(assets), $"'{assets}' is missing; restore the fixtures first.");

        byte[] original = await File.ReadAllBytesAsync(assets);
        try
        {
            await WorkspaceService.EvictAllAsync();
            RestoreWatcher.ResetForTests();
            using var armed = RestoreWatcher.ArmForTests();

            await WorkspaceService.GetOrOpenProjectAsync(project);
            Assert.True(
                await WaitForAsync(() => RestoreWatcher.WatchedDirectoryCount > 0, TimeSpan.FromSeconds(10)),
                "The load did not install a restore watcher.");

            await File.WriteAllBytesAsync(assets, original);

            // Several times the debounce, so a wrong answer here shows up as a failure rather than
            // as a race that passes on a fast machine.
            await Task.Delay(TimeSpan.FromSeconds(4));

            Assert.Empty(await WorkspaceService.ProjectsNotYetLoadedAsync([project]));
        }
        finally
        {
            RestoreWatcher.ResetForTests();
            await File.WriteAllBytesAsync(assets, original);
            await WorkspaceService.EvictAllAsync();
        }
    }

    private static Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout) =>
        WaitForAsync(() => Task.FromResult(condition()), timeout);

    private static async Task<bool> WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (await condition())
                return true;

            if (DateTime.UtcNow >= deadline)
                return false;

            await Task.Delay(200);
        }
    }
}

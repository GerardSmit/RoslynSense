using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>Launch targets, debugger provisioning, and the structured build behind F5.</summary>
/// <remarks>
/// Serialized: these open workspaces, and one of them writes the process-wide debug engine choice.
/// </remarks>
[Collection(SharedState.Name)]
public class LaunchHandlerTests
{
    [Fact]
    public async Task LaunchTargetsDescribeEveryProjectInTheLoadedSolution()
    {
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);

        var targets = await LaunchHandler.LaunchTargetsAsync(new LaunchTargetsParams(), default);

        Assert.NotEmpty(targets);
        var sample = targets.First(t =>
            string.Equals(t.ProjectPath, FixturePaths.SampleProjectFile, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("SampleProject", sample.ProjectName);
        Assert.NotNull(sample.TargetFramework);
    }

    [Fact]
    public async Task ATargetSaysWhetherItNeedsTheAdapterTheServerShips()
    {
        // The client cannot work this out for itself: the engine setting lives here, and a value
        // set in roslynsense.json never reaches the editor's own configuration at all.
        var restore = RoslynMCP.Config.DebugEngineOptions.CoreClr;
        try
        {
            await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);

            RoslynMCP.Config.DebugEngineOptions.CoreClr =
                RoslynMCP.Config.CoreClrDebugEngine.NetCoreDbg;
            var byDefault = Sample(
                await LaunchHandler.LaunchTargetsAsync(new LaunchTargetsParams(), default));

            // The fixture is .NET, so by default it is netcoredbg's — the flip below is what
            // carries this test, since "false for every target" would also hold if the field were
            // never stamped at all.
            Assert.False(byDefault.IsNetFramework);
            Assert.False(byDefault.ServerDebugAdapter);

            RoslynMCP.Config.DebugEngineOptions.CoreClr =
                RoslynMCP.Config.CoreClrDebugEngine.IcorDebug;
            var optedIn = Sample(
                await LaunchHandler.LaunchTargetsAsync(new LaunchTargetsParams(), default));

            Assert.True(optedIn.ServerDebugAdapter);

            static LaunchTarget Sample(IReadOnlyList<LaunchTarget> targets) =>
                targets.First(t => string.Equals(
                    t.ProjectPath, FixturePaths.SampleProjectFile, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            RoslynMCP.Config.DebugEngineOptions.CoreClr = restore;
        }
    }

    [Fact]
    public async Task NonRunnableProjectsAreReturnedWithAReasonRatherThanOmitted()
    {
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);

        var targets = await LaunchHandler.LaunchTargetsAsync(new LaunchTargetsParams(), default);

        // A picker that silently drops the project someone is looking for is worse than one
        // that says why it cannot be launched.
        Assert.All(targets, t => Assert.True(t.Runnable || t.Error is { Length: > 0 }));
    }

    [Fact]
    public async Task RunnableTargetsSortAheadOfBlockedOnes()
    {
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);

        var targets = await LaunchHandler.LaunchTargetsAsync(new LaunchTargetsParams(), default);

        int lastRunnable = Array.FindLastIndex(targets, t => t.Runnable);
        int firstBlocked = Array.FindIndex(targets, t => !t.Runnable);
        if (lastRunnable >= 0 && firstBlocked >= 0)
            Assert.True(lastRunnable < firstBlocked);
    }

    [Fact]
    public async Task BuildReportsStructuredErrorsForABrokenProject()
    {
        var result = await LaunchHandler.BuildAsync(FixturePaths.BrokenProjectFile, "Debug", default);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        // Structured, so the client can put them in Problems instead of a message box.
        var error = result.Errors[0];
        Assert.False(string.IsNullOrEmpty(error.File));
        Assert.True(error.Line > 0);
        Assert.Matches("^[A-Za-z]+[0-9]+$", error.Code!);
    }

    [Fact]
    public async Task BuildSucceedsForAHealthyProject()
    {
        var result = await LaunchHandler.BuildAsync(FixturePaths.SampleProjectFile, "Debug", default);

        Assert.True(result.Success, result.Summary);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task BuildOfMissingProjectFailsCleanly()
    {
        var result = await LaunchHandler.BuildAsync(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.csproj"), "Debug", default);

        Assert.False(result.Success);
        Assert.Contains("not found", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AttachTargetsListProcessesAndNeverIncludeTheServerItself()
    {
        var targets = LaunchHandler.AttachTargets();

        Assert.DoesNotContain(targets, t => t.Pid == Environment.ProcessId);
        Assert.All(targets, t => Assert.False(string.IsNullOrWhiteSpace(t.Name)));
        // Processes this server launched sort first so the common case is the top entry.
        int lastKnown = Array.FindLastIndex(targets, t => t.ProjectName is not null);
        int firstUnknown = Array.FindIndex(targets, t => t.ProjectName is null);
        if (lastKnown >= 0 && firstUnknown >= 0)
            Assert.True(lastKnown < firstUnknown);
    }

    [Fact]
    public async Task DebuggerPathResolvesOrExplainsWhyNot()
    {
        var result = await LaunchHandler.DebuggerPathAsync(default);

        if (result.Path is null)
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
        else
            Assert.True(File.Exists(result.Path), result.Path);
    }
}

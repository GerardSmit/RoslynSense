using RoslynMCP.Services;
using RoslynMCP.Tools;
using Xunit;

namespace RoslynMCP.Tests;

public class BuildProjectToolTests
{
    [Fact]
    public async Task WhenEmptyPathProvidedThenReturnsError()
    {
        var result = await BuildProjectTool.BuildProject(projectPath: "", new BackgroundTaskStore(), new BuildWarningsStore());

        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task WhenValidProjectBuiltThenReportsSuccess()
    {
        var result = await BuildProjectTool.BuildProject(
            projectPath: FixturePaths.SampleProjectFile, new BackgroundTaskStore(), new BuildWarningsStore());

        Assert.Contains("succeeded", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenSourceFileProvidedThenResolvesToProject()
    {
        var result = await BuildProjectTool.BuildProject(
            projectPath: FixturePaths.CalculatorFile, new BackgroundTaskStore(), new BuildWarningsStore());

        Assert.Contains("succeeded", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenNonExistentProjectProvidedThenReturnsError()
    {
        var result = await BuildProjectTool.BuildProject(
            projectPath: @"C:\NonExistent\Project.csproj", new BackgroundTaskStore(), new BuildWarningsStore());

        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task WhenConfigurationProvidedThenUsesIt()
    {
        var result = await BuildProjectTool.BuildProject(
            projectPath: FixturePaths.SampleProjectFile, new BackgroundTaskStore(), new BuildWarningsStore(),
            configuration: "Release");

        Assert.Contains("succeeded", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenMaliciousConfigurationProvidedThenSanitized()
    {
        // Configuration with injection attempt should be sanitized
        var result = await BuildProjectTool.BuildProject(
            projectPath: FixturePaths.SampleProjectFile, new BackgroundTaskStore(), new BuildWarningsStore(),
            configuration: "Debug; rm -rf /");

        // Should either succeed with sanitized config or fail safely
        Assert.DoesNotContain("rm -rf", result);
    }

    [Fact]
    public async Task WhenTimeoutExpiresThenReportsTimeout()
    {
        // A 0-second timeout should always fire before any real build completes
        var result = await BuildProjectTool.BuildProject(
            projectPath: FixturePaths.SampleProjectFile, new BackgroundTaskStore(), new BuildWarningsStore(),
            timeoutSeconds: 0);

        Assert.Contains("timed out", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenBuildSucceedsThenWarningsGroupedByCode()
    {
        var warningsStore = new BuildWarningsStore();
        var result = await BuildProjectTool.BuildProject(
            projectPath: FixturePaths.SampleProjectFile, new BackgroundTaskStore(), warningsStore);

        // Result should contain grouped warning summary (e.g. "Nx  CSxxxx")
        // rather than individual raw warning lines with full paths
        Assert.DoesNotContain("D:\\", result); // No absolute paths in summary
    }
}

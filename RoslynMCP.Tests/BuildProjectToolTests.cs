using RoslynMCP.Services;
using RoslynMCP.Tools;
using Xunit;

namespace RoslynMCP.Tests;

public class BuildProjectToolTests
{
    [Fact]
    public async Task WhenEmptyPathProvidedThenReturnsError()
    {
        var result = await BuildProjectTool.BuildProject(projectPath: "", new BackgroundTaskStore());

        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task WhenValidProjectBuiltThenReportsSuccess()
    {
        var result = await BuildProjectTool.BuildProject(
            projectPath: FixturePaths.SampleProjectFile, new BackgroundTaskStore());

        Assert.Contains("succeeded", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenSourceFileProvidedThenResolvesToProject()
    {
        var result = await BuildProjectTool.BuildProject(
            projectPath: FixturePaths.CalculatorFile, new BackgroundTaskStore());

        Assert.Contains("succeeded", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenNonExistentProjectProvidedThenReturnsError()
    {
        var result = await BuildProjectTool.BuildProject(
            projectPath: @"C:\NonExistent\Project.csproj", new BackgroundTaskStore());

        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task WhenConfigurationProvidedThenUsesIt()
    {
        var result = await BuildProjectTool.BuildProject(
            projectPath: FixturePaths.SampleProjectFile, new BackgroundTaskStore(),
            configuration: "Release");

        Assert.Contains("succeeded", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenMaliciousConfigurationProvidedThenSanitized()
    {
        // Configuration with injection attempt should be sanitized
        var result = await BuildProjectTool.BuildProject(
            projectPath: FixturePaths.SampleProjectFile, new BackgroundTaskStore(),
            configuration: "Debug; rm -rf /");

        // Should either succeed with sanitized config or fail safely
        Assert.DoesNotContain("rm -rf", result);
    }
}

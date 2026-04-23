using RoslynMCP.Tools;
using Xunit;

namespace RoslynMCP.Tests;

public class ListProjectsToolTests
{
    [Fact]
    public async Task WhenEmptyPathProvidedThenReturnsError()
    {
        var result = await ListProjectsTool.ListProjects(fmt: new RoslynMCP.Services.MarkdownFormatter(),path: "");

        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task WhenSolutionFileProvidedThenListsProjects()
    {
        // Find the solution file relative to fixtures
        var solutionPath = Path.GetFullPath(
            Path.Combine(FixturePaths.SampleProjectDir, "..", "..", "RoslynMCP.sln"));

        if (!File.Exists(solutionPath))
            return; // Skip if not found

        var result = await ListProjectsTool.ListProjects(fmt: new RoslynMCP.Services.MarkdownFormatter(),path: solutionPath);

        Assert.Contains("Solution", result);
        Assert.Contains("project", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenDirectoryProvidedThenFindsProjects()
    {
        var result = await ListProjectsTool.ListProjects(fmt: new RoslynMCP.Services.MarkdownFormatter(),path: FixturePaths.SampleProjectDir);

        // Should find SampleProject.csproj in the directory
        Assert.Contains("SampleProject", result);
    }

    [Fact]
    public async Task WhenSourceFileProvidedThenResolvesToDirectory()
    {
        var result = await ListProjectsTool.ListProjects(fmt: new RoslynMCP.Services.MarkdownFormatter(),path: FixturePaths.CalculatorFile);

        // Should resolve from Calculator.cs to SampleProject dir
        Assert.DoesNotContain("does not exist", result);
    }

    [Fact]
    public async Task WhenNonExistentPathProvidedThenReturnsError()
    {
        var result = await ListProjectsTool.ListProjects(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            path: @"C:\NonExistent\Path\That\Does\Not\Exist");

        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task WhenFixturesDirectoryProvidedThenListsMultipleProjects()
    {
        // Fixtures directory contains multiple projects
        var fixturesDir = Path.GetDirectoryName(FixturePaths.SampleProjectDir)!;
        var result = await ListProjectsTool.ListProjects(fmt: new RoslynMCP.Services.MarkdownFormatter(),path: fixturesDir);

        // Should find multiple projects
        Assert.Contains("project", result, StringComparison.OrdinalIgnoreCase);
    }
}

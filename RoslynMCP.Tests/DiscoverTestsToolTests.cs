using Xunit;

namespace RoslynMCP.Tests;

public class DiscoverTestsToolTests
{
    [Fact]
    public async Task WhenProjectPathIsEmptyThenReturnsError()
    {
        var result = await RoslynMCP.Tools.DiscoverTestsTool.DiscoverTests("");
        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task WhenProjectDoesNotExistThenReturnsError()
    {
        var result = await RoslynMCP.Tools.DiscoverTestsTool.DiscoverTests(@"C:\nonexistent\project.csproj");
        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task WhenDiscoveringTestProjectThenFindsTests()
    {
        var testProjectPath = FindTestProjectPath();
        if (testProjectPath is null) return;

        var result = await RoslynMCP.Tools.DiscoverTestsTool.DiscoverTests(testProjectPath);

        Assert.Contains("Test Discovery", result);
        Assert.Contains("test method(s)", result);
        Assert.Contains("xUnit", result);
    }

    [Fact]
    public async Task WhenFilteringByClassNameThenFiltersResults()
    {
        var testProjectPath = FindTestProjectPath();
        if (testProjectPath is null) return;

        var result = await RoslynMCP.Tools.DiscoverTestsTool.DiscoverTests(
            testProjectPath, className: "DiscoverTestsToolTests");

        Assert.Contains("DiscoverTestsToolTests", result);
        // Should find at least the tests in this class
        Assert.Contains("WhenProjectPathIsEmptyThenReturnsError", result);
    }

    [Fact]
    public async Task WhenFilteringByNonexistentClassThenReturnsNoTests()
    {
        var testProjectPath = FindTestProjectPath();
        if (testProjectPath is null) return;

        var result = await RoslynMCP.Tools.DiscoverTestsTool.DiscoverTests(
            testProjectPath, className: "NonExistentClassName12345");

        Assert.Contains("No test methods found", result);
    }

    [Fact]
    public async Task WhenDiscoveringNonTestProjectThenReturnsNoTests()
    {
        var result = await RoslynMCP.Tools.DiscoverTestsTool.DiscoverTests(FixturePaths.SampleProjectFile);

        Assert.Contains("No test methods found", result);
    }

    [Fact]
    public async Task WhenTestsDiscoveredThenOutputShowsLineRanges()
    {
        var testProjectPath = FindTestProjectPath();
        if (testProjectPath is null) return;

        var result = await RoslynMCP.Tools.DiscoverTestsTool.DiscoverTests(
            testProjectPath, className: "DiscoverTestsToolTests");

        // Test methods span multiple lines (attribute + body), so should show X–Y ranges
        Assert.Matches(@"\d+–\d+", result);
    }

    private static string? FindTestProjectPath()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        while (dir is not null)
        {
            var csproj = Path.Combine(dir, "RoslynMCP.Tests.csproj");
            if (File.Exists(csproj)) return csproj;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}

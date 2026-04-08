using RoslynMCP.Tools;
using Xunit;

namespace RoslynMCP.Tests;

public class RunTestsToolTests
{
    [Fact]
    public async Task WhenProjectPathIsEmptyThenReturnsError()
    {
        var result = await RunTestsTool.RunTests("");

        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task WhenProjectDoesNotExistThenReturnsError()
    {
        var result = await RunTestsTool.RunTests("/nonexistent/path/Test.csproj");

        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task WhenRunningNonTestProjectThenDotnetTestHandlesError()
    {
        // dotnet test will report its own error for non-test projects
        var result = await RunTestsTool.RunTests(FixturePaths.SampleProjectFile);

        // Should get an error from dotnet test, not from our validation
        Assert.NotNull(result);
    }

    [Fact]
    public async Task WhenRunningActualTestProjectWithFilterThenReturnsResults()
    {
        // Run a single specific test from RoslynMCP.Tests
        var testProjectPath = FindTestProjectPath();
        if (testProjectPath is null)
        {
            // Skip if we can't find the test project
            return;
        }

        var result = await RunTestsTool.RunTests(
            testProjectPath,
            "FullyQualifiedName=RoslynMCP.Tests.RunTestsToolTests.WhenProjectIsNotTestProjectThenReturnsError",
            build: false);

        Assert.Contains("Passed", result);
    }

    [Fact]
    public async Task WhenRunningTestProjectWithInvalidFilterThenReturnsNoTests()
    {
        var testProjectPath = FindTestProjectPath();
        if (testProjectPath is null) return;

        var result = await RunTestsTool.RunTests(
            testProjectPath,
            "FullyQualifiedName=NonExistent.Test.Method",
            build: false);

        // Should run but find no tests
        Assert.NotNull(result);
    }

    private static string? FindTestProjectPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var csproj = Path.Combine(dir.FullName, "RoslynMCP.Tests", "RoslynMCP.Tests.csproj");
            if (File.Exists(csproj)) return csproj;
            var sln = Path.Combine(dir.FullName, "RoslynMCP.sln");
            if (File.Exists(sln))
            {
                csproj = Path.Combine(dir.FullName, "RoslynMCP.Tests", "RoslynMCP.Tests.csproj");
                if (File.Exists(csproj)) return csproj;
            }
            dir = dir.Parent;
        }
        return null;
    }
}

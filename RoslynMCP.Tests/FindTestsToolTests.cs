using RoslynMCP.Services;
using RoslynMCP.Tools;
using Xunit;

namespace RoslynMCP.Tests;

public class FindTestsToolTests
{
    [Fact]
    public async Task WhenSymbolHasNoTestReferencesThenReturnsNoTestsMessage()
    {
        var result = await FindTestsTool.FindTests(
            FixturePaths.CalculatorFile,
            "public int [|Add|](int a, int b)");

        Assert.Contains("No test methods found", result);
    }

    [Fact]
    public async Task WhenFilePathIsEmptyThenReturnsError()
    {
        var result = await FindTestsTool.FindTests("", "void [|Test|]()");

        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task WhenMarkupIsInvalidThenReturnsError()
    {
        var result = await FindTestsTool.FindTests(
            FixturePaths.CalculatorFile,
            "no markup here");

        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task WhenFileDoesNotExistThenReturnsError()
    {
        var result = await FindTestsTool.FindTests(
            "/nonexistent/path/file.cs",
            "void [|Test|]()");

        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task WhenSymbolNotFoundThenReturnsError()
    {
        var result = await FindTestsTool.FindTests(
            FixturePaths.CalculatorFile,
            "void [|NonExistentMethod|]()");

        Assert.Contains("not found", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindReferencingProjects_FindsTestProject()
    {
        // RoslynMCP.Tests has a <ProjectReference> to RoslynMCP
        var roslynMcpProject = Path.GetFullPath(
            Path.Combine(FixturePaths.SampleProjectDir, "..", "..", "..", "RoslynMCP", "RoslynMCP.csproj"));

        var referencingProjects = WorkspaceService.FindReferencingProjects(roslynMcpProject);

        Assert.NotEmpty(referencingProjects);
        Assert.Contains(referencingProjects, p =>
            p.Contains("RoslynMCP.Tests", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FindReferencingProjects_DoesNotIncludeSelf()
    {
        var roslynMcpProject = Path.GetFullPath(
            Path.Combine(FixturePaths.SampleProjectDir, "..", "..", "..", "RoslynMCP", "RoslynMCP.csproj"));

        var referencingProjects = WorkspaceService.FindReferencingProjects(roslynMcpProject);

        Assert.DoesNotContain(referencingProjects, p =>
            p.EndsWith("RoslynMCP.csproj", StringComparison.OrdinalIgnoreCase) &&
            !p.Contains("Tests", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FindReferencingProjects_NonReferencedProjectReturnsEmpty()
    {
        // BrokenProject is not referenced by any other project
        var referencingProjects = WorkspaceService.FindReferencingProjects(
            FixturePaths.BrokenProjectFile);

        Assert.Empty(referencingProjects);
    }
}

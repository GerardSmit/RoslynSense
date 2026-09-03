using RoslynMCP.Resources;
using Xunit;

namespace RoslynMCP.Tests;

public class ProjectStructureResourceTests
{
    [Fact]
    public async Task WhenEmptyPathProvidedThenReturnsError()
    {
        var result = await ProjectStructureResource.GetProjectStructureAsync("");

        Assert.Contains("Error", result);
        Assert.Contains("empty", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenNonExistentFileProvidedThenReturnsError()
    {
        var result = await ProjectStructureResource.GetProjectStructureAsync("Z:/nonexistent/file.cs");

        Assert.Contains("Error", result);
        Assert.Contains("does not exist", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenCalculatorFileProvidedThenReturnsProjectName()
    {
        var result = await ProjectStructureResource.GetProjectStructureAsync(FixturePaths.CalculatorFile);

        Assert.Contains("**Project: SampleProject**", result);
    }

    [Fact]
    public async Task WhenCalculatorFileProvidedThenReturnsProjectPath()
    {
        var result = await ProjectStructureResource.GetProjectStructureAsync(FixturePaths.CalculatorFile);

        Assert.Contains("SampleProject.csproj", result);
    }

    [Fact]
    public async Task WhenCalculatorFileProvidedThenListsCalculatorFile()
    {
        var result = await ProjectStructureResource.GetProjectStructureAsync(FixturePaths.CalculatorFile);

        Assert.Contains("Calculator.cs", result);
    }

    [Fact]
    public async Task WhenCalculatorFileProvidedThenListsWarningsFile()
    {
        var result = await ProjectStructureResource.GetProjectStructureAsync(FixturePaths.CalculatorFile);

        Assert.Contains("Warnings.cs", result);
    }

    [Fact]
    public async Task WhenCalculatorFileProvidedThenListsResultFile()
    {
        var result = await ProjectStructureResource.GetProjectStructureAsync(FixturePaths.CalculatorFile);

        Assert.Contains("Result.cs", result);
    }

    [Fact]
    public async Task WhenCalculatorFileProvidedThenShowsDocumentCount()
    {
        var result = await ProjectStructureResource.GetProjectStructureAsync(FixturePaths.CalculatorFile);

        Assert.Contains("Documents", result);
    }

    [Fact]
    public async Task WhenCalculatorFileProvidedThenShowsModelsDirectory()
    {
        var result = await ProjectStructureResource.GetProjectStructureAsync(FixturePaths.CalculatorFile);

        Assert.Contains("Models", result);
    }
}

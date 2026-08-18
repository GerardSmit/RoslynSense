using RoslynMCP.Services;
using RoslynMCP.Tools;
using Xunit;

namespace RoslynMCP.Tests;

[Collection(SharedState.Name)]
public class GoToDefinitionToolTests
{
    [Fact]
    public async Task WhenEmptySymbolNameThenReturnsError()
    {
        var result = await GoToDefinitionTool.GoToDefinition(
            filePath: FixturePaths.CalculatorFile, symbolName: "", fmt: new MarkdownFormatter());

        Assert.Contains("Error", result);
        Assert.Contains("empty", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenFrameworkTypeTargetedThenReturnsDecompiledSource()
    {
        using var offline = ExternalSourceScope.Offline();

        var result = await GoToDefinitionTool.GoToDefinition(
            filePath: FixturePaths.CalculatorFile,
            symbolName: "System.Object",
            fmt: new MarkdownFormatter());

        Assert.Contains("Definition: Object", result);
        Assert.Contains("System", result);
        Assert.Contains("Decompiled Source", result);
    }

    [Fact]
    public async Task WhenFrameworkMethodTargetedThenReturnsDefinition()
    {
        var result = await GoToDefinitionTool.GoToDefinition(
            filePath: FixturePaths.FrameworkReferencesFile,
            symbolName: "System.Console.WriteLine",
            fmt: new MarkdownFormatter());

        Assert.Contains("Definition: WriteLine", result);
        Assert.Contains("System.Console", result);
    }

    [Fact]
    public async Task WhenSourceTypeTargetedThenReturnsSourceLocation()
    {
        var result = await GoToDefinitionTool.GoToDefinition(
            filePath: FixturePaths.CalculatorFile,
            symbolName: "SampleProject.Calculator",
            fmt: new MarkdownFormatter());

        Assert.Contains("Definition: Calculator", result);
        Assert.Contains("Calculator.cs", result);
        Assert.Contains("## Members", result);
    }

    [Fact]
    public async Task WhenSourceMethodTargetedThenReturnsDefinition()
    {
        var result = await GoToDefinitionTool.GoToDefinition(
            filePath: FixturePaths.CalculatorFile,
            symbolName: "SampleProject.Calculator.Add",
            fmt: new MarkdownFormatter());

        Assert.Contains("Definition: Add", result);
        Assert.Contains("Method", result);
    }

    [Fact]
    public async Task WhenGenericTypeTargetedThenReturnsDefinition()
    {
        var result = await GoToDefinitionTool.GoToDefinition(
            filePath: FixturePaths.CalculatorFile,
            symbolName: "System.Collections.Generic.List`1",
            fmt: new MarkdownFormatter());

        Assert.Contains("Definition: List", result);
        Assert.Contains("System.Collections.Generic", result);
    }

    [Fact]
    public async Task WhenNonExistentSymbolThenReturnsError()
    {
        var result = await GoToDefinitionTool.GoToDefinition(
            filePath: FixturePaths.CalculatorFile,
            symbolName: "System.DoesNotExist",
            fmt: new MarkdownFormatter());

        Assert.Contains("Error", result);
        Assert.Contains("not found", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenStringContainsMethodTargetedThenReturnsDefinition()
    {
        var result = await GoToDefinitionTool.GoToDefinition(
            filePath: FixturePaths.CalculatorFile,
            symbolName: "System.String.Contains",
            fmt: new MarkdownFormatter());

        Assert.Contains("Definition: Contains", result);
        Assert.Contains("string", result);
    }

    [Fact]
    public async Task WhenPropertyTargetedThenReturnsDefinition()
    {
        var result = await GoToDefinitionTool.GoToDefinition(
            filePath: FixturePaths.CalculatorFile,
            symbolName: "System.String.Length",
            fmt: new MarkdownFormatter());

        Assert.Contains("Definition: Length", result);
        Assert.Contains("Property", result);
    }
}

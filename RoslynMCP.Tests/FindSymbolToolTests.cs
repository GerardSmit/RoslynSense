using RoslynMCP.Tools;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Tests for symbol search functionality via SemanticSymbolSearch.
/// Originally tested FindSymbol which was merged into SemanticSymbolSearch.
/// </summary>
public class FindSymbolToolTests
{
    [Fact]
    public async Task WhenEmptyPathProvidedThenReturnsError()
    {
        var result = await SemanticSymbolSearchTool.SemanticSymbolSearch(fmt: new RoslynMCP.Services.MarkdownFormatter(),filePath: "", query: "Add");

        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task WhenEmptyQueryProvidedThenReturnsError()
    {
        var result = await SemanticSymbolSearchTool.SemanticSymbolSearch(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.CalculatorFile, query: "");

        Assert.Contains("Error", result);
        Assert.Contains("empty", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenMethodNameSearchedThenFindsMethod()
    {
        var result = await SemanticSymbolSearchTool.SemanticSymbolSearch(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.CalculatorFile, query: "Add");

        Assert.Contains("Add", result);
        Assert.Contains("Calculator.cs", result);
    }

    [Fact]
    public async Task WhenTypeNameSearchedThenFindsType()
    {
        var result = await SemanticSymbolSearchTool.SemanticSymbolSearch(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.CalculatorFile, query: "Calculator");

        Assert.Contains("Calculator", result);
    }

    [Fact]
    public async Task WhenNonExistentSymbolSearchedThenReportsNoResults()
    {
        var result = await SemanticSymbolSearchTool.SemanticSymbolSearch(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.CalculatorFile, query: "ZZZDoesNotExistZZZ");

        Assert.Contains("No", result);
    }

    [Fact]
    public async Task WhenPatternMatchedThenFindsSymbol()
    {
        var result = await SemanticSymbolSearchTool.SemanticSymbolSearch(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.CalculatorFile, query: "Calc");

        Assert.Contains("Calculator", result);
    }

    [Fact]
    public async Task WhenResultTypeSearchedThenFindsRecordInOtherFile()
    {
        var result = await SemanticSymbolSearchTool.SemanticSymbolSearch(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.CalculatorFile, query: "Result");

        Assert.Contains("Result", result);
    }

    [Fact]
    public async Task WhenNonExistentFileProvidedThenReturnsError()
    {
        var result = await SemanticSymbolSearchTool.SemanticSymbolSearch(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: Path.Combine(FixturePaths.SampleProjectDir, "Ghost.cs"),
            query: "Something");

        Assert.Contains("does not exist", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenPropertySearchedThenFindsProperty()
    {
        var result = await SemanticSymbolSearchTool.SemanticSymbolSearch(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.CalculatorFile, query: "Value");

        Assert.Contains("Value", result);
    }

    [Fact]
    public async Task WhenInterfaceSearchedThenFindsInterface()
    {
        var result = await SemanticSymbolSearchTool.SemanticSymbolSearch(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.ServicesFile, query: "IStringFormatter");

        Assert.Contains("IStringFormatter", result);
    }

    [Fact]
    public async Task WhenEventSearchedThenFindsEvent()
    {
        var result = await SemanticSymbolSearchTool.SemanticSymbolSearch(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.OutlineShowcaseFile, query: "Changed");

        Assert.Contains("Changed", result);
    }

    [Fact]
    public async Task WhenDelegateSearchedThenFindsDelegate()
    {
        var result = await SemanticSymbolSearchTool.SemanticSymbolSearch(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.OutlineShowcaseFile, query: "ValueFormatter");

        Assert.Contains("ValueFormatter", result);
    }

    [Fact]
    public async Task WhenConstantSearchedThenFindsField()
    {
        var result = await SemanticSymbolSearchTool.SemanticSymbolSearch(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.OutlineShowcaseFile, query: "DefaultValue");

        Assert.Contains("DefaultValue", result);
    }
}

using RoslynMCP.Tools;
using Xunit;

namespace RoslynMCP.Tests;

public class FindSymbolToolTests
{
    [Fact]
    public async Task WhenEmptyPathProvidedThenReturnsError()
    {
        var result = await FindSymbolTool.FindSymbol(filePath: "", symbolName: "Add");

        Assert.Contains("Error", result);
        Assert.Contains("empty", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenEmptySymbolNameProvidedThenReturnsError()
    {
        var result = await FindSymbolTool.FindSymbol(
            filePath: FixturePaths.CalculatorFile, symbolName: "");

        Assert.Contains("Error", result);
        Assert.Contains("empty", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenMethodNameSearchedThenFindsMethod()
    {
        var result = await FindSymbolTool.FindSymbol(
            filePath: FixturePaths.CalculatorFile, symbolName: "Add");

        Assert.Contains("Symbol Search", result);
        Assert.Contains("Add", result);
        Assert.Contains("Method", result);
        Assert.Contains("Calculator.cs", result);
    }

    [Fact]
    public async Task WhenTypeNameSearchedThenFindsType()
    {
        var result = await FindSymbolTool.FindSymbol(
            filePath: FixturePaths.CalculatorFile, symbolName: "Calculator");

        Assert.Contains("Calculator", result);
        Assert.Contains("NamedType", result);
    }

    [Fact]
    public async Task WhenNonExistentSymbolSearchedThenReportsNoResults()
    {
        var result = await FindSymbolTool.FindSymbol(
            filePath: FixturePaths.CalculatorFile, symbolName: "ZZZDoesNotExistZZZ");

        Assert.Contains("No matching symbols found", result);
    }

    [Fact]
    public async Task WhenPatternMatchedThenFindsMultipleSymbols()
    {
        // "Calc" should match "Calculator" via substring/pattern matching
        var result = await FindSymbolTool.FindSymbol(
            filePath: FixturePaths.CalculatorFile, symbolName: "Calc");

        Assert.Contains("Calculator", result);
    }

    [Fact]
    public async Task WhenResultTypeSearchedThenFindsRecordInOtherFile()
    {
        var result = await FindSymbolTool.FindSymbol(
            filePath: FixturePaths.CalculatorFile, symbolName: "Result");

        Assert.Contains("Result", result);
        Assert.Contains("Result.cs", result);
    }

    [Fact]
    public async Task WhenNonExistentFileProvidedThenReturnsError()
    {
        var result = await FindSymbolTool.FindSymbol(
            filePath: Path.Combine(FixturePaths.SampleProjectDir, "Ghost.cs"),
            symbolName: "Something");

        Assert.Contains("does not exist", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenPropertySearchedThenFindsProperty()
    {
        var result = await FindSymbolTool.FindSymbol(
            filePath: FixturePaths.CalculatorFile, symbolName: "Value");

        Assert.Contains("Value", result);
        Assert.Contains("Property", result);
    }

    [Fact]
    public async Task WhenSearchResultsFoundThenShowsTableFormat()
    {
        var result = await FindSymbolTool.FindSymbol(
            filePath: FixturePaths.CalculatorFile, symbolName: "Add");

        Assert.Contains("Symbol Search", result);
        Assert.Contains("| # |", result);
        Assert.Contains("Symbol", result);
        Assert.Contains("Kind", result);
    }

    [Fact]
    public async Task WhenEnumSearchedThenFindsEnumType()
    {
        var result = await FindSymbolTool.FindSymbol(
            filePath: FixturePaths.ServicesFile, symbolName: "ProcessingStatus");

        Assert.Contains("ProcessingStatus", result);
        Assert.Contains("NamedType", result);
    }

    [Fact]
    public async Task WhenInterfaceSearchedThenFindsInterface()
    {
        var result = await FindSymbolTool.FindSymbol(
            filePath: FixturePaths.ServicesFile, symbolName: "IStringFormatter");

        Assert.Contains("IStringFormatter", result);
        Assert.Contains("NamedType", result);
    }

    [Fact]
    public async Task WhenEventSearchedThenFindsEvent()
    {
        var result = await FindSymbolTool.FindSymbol(
            filePath: FixturePaths.OutlineShowcaseFile, symbolName: "Changed");

        Assert.Contains("Changed", result);
        Assert.Contains("Event", result);
    }

    [Fact]
    public async Task WhenDelegateSearchedThenFindsDelegate()
    {
        var result = await FindSymbolTool.FindSymbol(
            filePath: FixturePaths.OutlineShowcaseFile, symbolName: "ValueFormatter");

        Assert.Contains("ValueFormatter", result);
        Assert.Contains("NamedType", result);
    }

    [Fact]
    public async Task WhenConstantSearchedThenFindsField()
    {
        var result = await FindSymbolTool.FindSymbol(
            filePath: FixturePaths.OutlineShowcaseFile, symbolName: "DefaultValue");

        Assert.Contains("DefaultValue", result);
        Assert.Contains("Field", result);
    }
}
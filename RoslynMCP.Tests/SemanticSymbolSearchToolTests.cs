using RoslynMCP.Tools;
using Xunit;

namespace RoslynMCP.Tests;

public class SemanticSymbolSearchToolTests
{
    [Fact]
    public async Task WhenEmptyPathProvidedThenReturnsError()
    {
        var result = await SemanticSymbolSearchTool.SemanticSymbolSearch(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: "", query: "Add");

        Assert.Contains("Error", result);
        Assert.Contains("empty", result, StringComparison.OrdinalIgnoreCase);
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
    public async Task WhenNonExistentFileProvidedThenReturnsError()
    {
        var result = await SemanticSymbolSearchTool.SemanticSymbolSearch(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: @"C:\nonexistent\file.cs", query: "Add");

        Assert.Contains("Error", result);
        Assert.Contains("does not exist", result);
    }

    [Fact]
    public async Task WhenExactNameSearchedThenFindsSymbol()
    {
        var result = await SemanticSymbolSearchTool.SemanticSymbolSearch(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.CalculatorFile, query: "Add");

        Assert.Contains("Semantic Symbol Search", result);
        Assert.Contains("Add", result);
        Assert.Contains("Method", result);
    }

    [Fact]
    public async Task WhenTypeSearchedThenFindsNamedType()
    {
        var result = await SemanticSymbolSearchTool.SemanticSymbolSearch(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.CalculatorFile, query: "Calculator");

        Assert.Contains("Calculator", result);
        Assert.Contains("NamedType", result);
    }

    [Fact]
    public async Task WhenNonExistentSymbolSearchedThenReportsNoResults()
    {
        var result = await SemanticSymbolSearchTool.SemanticSymbolSearch(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.CalculatorFile, query: "ZZZDoesNotExistZZZ");

        Assert.Contains("No matching symbols found", result);
    }

    [Fact]
    public async Task WhenPrefixSearchedThenMatchesMultipleSymbols()
    {
        // "Compute" should match ComputeAverageSum and Compute
        var result = await SemanticSymbolSearchTool.SemanticSymbolSearch(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.CalculatorFile, query: "Compute");

        Assert.Contains("Compute", result);
    }

    [Fact]
    public async Task WhenSubstringSearchedThenMatchesContainingSymbols()
    {
        // "Result" should match Result (type) and AddResult (method)
        var result = await SemanticSymbolSearchTool.SemanticSymbolSearch(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.CalculatorFile, query: "Result");

        Assert.Contains("Result", result);
    }

    [Fact]
    public async Task WhenCamelCasePatternUsedThenFindsMatch()
    {
        // "CAS" should match "ComputeAverageSum" via camelCase
        var result = await SemanticSymbolSearchTool.SemanticSymbolSearch(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.CalculatorFile, query: "CAS");

        Assert.Contains("ComputeAverageSum", result);
        Assert.Contains("camelCase", result);
    }

    [Fact]
    public async Task WhenSearchedThenResultsIncludeLocationInfo()
    {
        var result = await SemanticSymbolSearchTool.SemanticSymbolSearch(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.CalculatorFile, query: "Add");

        // Should include a full file path with line number
        Assert.Contains("Calculator.cs:", result);
    }

    [Fact]
    public async Task WhenSearchedThenResultsIncludeMatchReason()
    {
        var result = await SemanticSymbolSearchTool.SemanticSymbolSearch(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.CalculatorFile, query: "Add");

        // At least one result should have an "exact name" reason
        Assert.Contains("exact name", result);
    }

    [Fact]
    public async Task WhenSearchedThenSymbolsFromOtherFilesAppear()
    {
        // Searching from Calculator.cs should also find symbols in Services.cs
        var result = await SemanticSymbolSearchTool.SemanticSymbolSearch(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.CalculatorFile, query: "StatisticsCalculator");

        Assert.Contains("StatisticsCalculator", result);
        Assert.Contains("Services.cs", result);
    }

    [Fact]
    public async Task WhenInterfaceSearchedThenFindsInterface()
    {
        var result = await SemanticSymbolSearchTool.SemanticSymbolSearch(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.CalculatorFile, query: "IStringFormatter");

        Assert.Contains("IStringFormatter", result);
        Assert.Contains("NamedType", result);
    }

    [Fact]
    public async Task WhenEnumSearchedThenFindsEnum()
    {
        var result = await SemanticSymbolSearchTool.SemanticSymbolSearch(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.CalculatorFile, query: "ProcessingStatus");

        Assert.Contains("ProcessingStatus", result);
        Assert.Contains("NamedType", result);
    }

    [Fact]
    public async Task WhenMaxResultsLimitedThenRespectsCap()
    {
        var result = await SemanticSymbolSearchTool.SemanticSymbolSearch(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.CalculatorFile, query: "a", maxResults: 3);

        // Count table rows (lines starting with "| " and a digit)
        int rowCount = result.Split('\n')
            .Count(line => line.TrimStart().StartsWith("| ") &&
                           line.TrimStart().Length > 2 &&
                           char.IsDigit(line.TrimStart()[2]));

        Assert.True(rowCount <= 3, $"Expected at most 3 rows but found {rowCount}");
    }

    [Fact]
    public async Task WhenExactMatchExistsThenItRanksFirst()
    {
        var result = await SemanticSymbolSearchTool.SemanticSymbolSearch(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.CalculatorFile, query: "Add");

        // The first result row should contain "Add" as an exact name match
        var lines = result.Split('\n');
        var firstRow = lines.FirstOrDefault(l =>
            l.TrimStart().StartsWith("| 1 "));

        Assert.NotNull(firstRow);
        Assert.Contains("Add", firstRow);
        Assert.Contains("exact name", firstRow);
    }

    [Fact]
    public async Task WhenReferencedAssembliesEnabledThenSearchesMetadata()
    {
        var result = await SemanticSymbolSearchTool.SemanticSymbolSearch(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.CalculatorFile,
            query: "Console",
            includeReferencedAssemblies: true);

        Assert.Contains("referenced assemblies", result);
        // Should find System.Console or similar from framework references
        Assert.Contains("Console", result);
    }

    [Fact]
    public async Task WhenSearchedThenOutputIncludesFollowUpTip()
    {
        var result = await SemanticSymbolSearchTool.SemanticSymbolSearch(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.CalculatorFile, query: "Add");

        Assert.Contains("GoToDefinition", result);
        Assert.Contains("FindUsages", result);
    }

    [Fact]
    public async Task WhenSearchedThenOutputShowsProjectCount()
    {
        var result = await SemanticSymbolSearchTool.SemanticSymbolSearch(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.CalculatorFile, query: "Calculator");

        Assert.Contains("source project(s)", result);
    }

    [Fact]
    public async Task WhenDocumentedSymbolMatchesQueryTermThenDocBoostApplied()
    {
        // "statistics" appears in StatisticsCalculator's XML doc
        var result = await SemanticSymbolSearchTool.SemanticSymbolSearch(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.CalculatorFile, query: "statistics");

        Assert.Contains("StatisticsCalculator", result);
        Assert.Contains("in docs", result);
    }

    [Fact]
    public async Task WhenDocsProvideOnlyStrongSignalThenFindsDocumentedMethod()
    {
        var result = await SemanticSymbolSearchTool.SemanticSymbolSearch(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.TextUtilitiesFile, query: "provided text");

        Assert.Contains("UppercaseFirstCharacter", result);
        Assert.Contains("docs", result);
    }

    [Fact]
    public async Task WhenSourceBodyProvidesOnlyStrongSignalThenFindsUndocumentedMethod()
    {
        var result = await SemanticSymbolSearchTool.SemanticSymbolSearch(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.TextUtilitiesFile, query: "trim first character");

        Assert.Contains("NormalizeLabel", result);
        Assert.Contains("source cues", result);
    }

    [Fact]
    public async Task WhenSignatureTermsMatchThenReasonIncludesSignature()
    {
        var result = await SemanticSymbolSearchTool.SemanticSymbolSearch(fmt: new RoslynMCP.Services.MarkdownFormatter(),
            filePath: FixturePaths.CalculatorFile, query: "integer value");

        Assert.Contains("FormatDisplayValue", result);
        Assert.Contains("signature", result);
    }

    [Theory]
    [InlineData("results", "result")]
    [InlineData("analysis", "analysis")]
    [InlineData("status", "status")]
    [InlineData("bonus", "bonus")]
    public void WhenNormalizingTokensThenOnlyPlainPluralSuffixIsTrimmed(string input, string expected)
    {
        Assert.Equal(expected, SemanticSymbolSearchTool.NormalizeTokenForSearch(input));
    }
}

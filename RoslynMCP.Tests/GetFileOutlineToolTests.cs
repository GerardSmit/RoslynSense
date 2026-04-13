using RoslynMCP.Services;
using RoslynMCP.Tools;
using Xunit;

namespace RoslynMCP.Tests;

public class GetFileOutlineToolTests
{
    [Fact]
    public async Task WhenEmptyPathProvidedThenReturnsError()
    {
        var result = await GetFileOutlineTool.GetFileOutline(filePath: "", fmt: new MarkdownFormatter());

        Assert.Contains("Error", result);
        Assert.Contains("empty", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenNonExistentFileProvidedThenReturnsError()
    {
        var result = await GetFileOutlineTool.GetFileOutline(filePath: "Z:/nonexistent/file.cs", fmt: new MarkdownFormatter());

        Assert.Contains("Error", result);
        Assert.Contains("does not exist", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenCalculatorFileProvidedThenShowsNamespace()
    {
        var result = await GetFileOutlineTool.GetFileOutline(filePath: FixturePaths.CalculatorFile, fmt: new MarkdownFormatter());

        Assert.Contains("namespace SampleProject", result);
    }

    [Fact]
    public async Task WhenCalculatorFileProvidedThenShowsClassDeclaration()
    {
        var result = await GetFileOutlineTool.GetFileOutline(filePath: FixturePaths.CalculatorFile, fmt: new MarkdownFormatter());

        Assert.Contains("public class Calculator", result);
    }

    [Fact]
    public async Task WhenCalculatorFileProvidedThenShowsMethods()
    {
        var result = await GetFileOutlineTool.GetFileOutline(filePath: FixturePaths.CalculatorFile, fmt: new MarkdownFormatter());

        Assert.Contains("Add", result);
        Assert.Contains("Subtract", result);
        Assert.Contains("Compute", result);
    }

    [Fact]
    public async Task WhenCalculatorFileProvidedThenShowsLineNumbers()
    {
        var result = await GetFileOutlineTool.GetFileOutline(filePath: FixturePaths.CalculatorFile, fmt: new MarkdownFormatter());

        // Line numbers should appear as "N:" prefix
        Assert.Matches(@"\d+:\s+", result);
    }

    [Fact]
    public async Task WhenRecordFileProvidedThenShowsRecordDeclaration()
    {
        var result = await GetFileOutlineTool.GetFileOutline(filePath: FixturePaths.ResultFile, fmt: new MarkdownFormatter());

        Assert.Contains("record", result);
        Assert.Contains("Result", result);
    }

    [Fact]
    public async Task WhenCalculatorFileProvidedThenIncludesOutlineHeader()
    {
        var result = await GetFileOutlineTool.GetFileOutline(filePath: FixturePaths.CalculatorFile, fmt: new MarkdownFormatter());

        Assert.Contains("# Outline:", result);
        Assert.Contains("Calculator.cs", result);
    }

    [Fact]
    public async Task WhenOutlineShowcaseProvidedThenTopLevelDeclarationsAreIncluded()
    {
        var result = await GetFileOutlineTool.GetFileOutline(filePath: FixturePaths.OutlineShowcaseFile, fmt: new MarkdownFormatter());

        Assert.Contains("delegate string ValueFormatter(int value)", result);
        Assert.Contains("public enum OutlineKind", result);
        Assert.Contains("public record OutlineRecord(int Value)", result);
    }

    [Fact]
    public async Task WhenOutlineShowcaseProvidedThenFieldsPropertiesAndEventsAreIncluded()
    {
        var result = await GetFileOutlineTool.GetFileOutline(filePath: FixturePaths.OutlineShowcaseFile, fmt: new MarkdownFormatter());

        Assert.Contains("public const int DefaultValue", result);
        Assert.Contains("private readonly List<int> _values", result);
        Assert.Contains("public event EventHandler? Changed", result);
        Assert.Contains("public event EventHandler? Routed", result);
        Assert.Contains("public string Name { get; init; }", result);
        Assert.Contains("public int this[int index] { get; set; }", result);
    }

    [Fact]
    public async Task WhenOutlineShowcaseProvidedThenConstructorsAndOperatorsAreIncluded()
    {
        var result = await GetFileOutlineTool.GetFileOutline(filePath: FixturePaths.OutlineShowcaseFile, fmt: new MarkdownFormatter());

        Assert.Contains("public OutlineShowcase()", result);
        Assert.Contains("~OutlineShowcase()", result);
        Assert.Contains("public static OutlineShowcase operator +(OutlineShowcase left, OutlineShowcase right)", result);
        Assert.Contains("public static implicit operator int(OutlineShowcase value)", result);
    }

    [Fact]
    public async Task WhenAspxFileProvidedThenReturnsAspxOutline()
    {
        var result = await GetFileOutlineTool.GetFileOutline(filePath: FixturePaths.DefaultAspxFile, fmt: new MarkdownFormatter(), handlers: TestHandlers.Outline);

        Assert.Contains("Directives", result, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.StartsWith("Error:"), "Unexpected tool-level error: " + result[..Math.Min(200, result.Length)]);
    }

    [Fact]
    public async Task WhenAscxFileProvidedThenReturnsAspxOutline()
    {
        var result = await GetFileOutlineTool.GetFileOutline(filePath: FixturePaths.HeaderControlFile, fmt: new MarkdownFormatter(), handlers: TestHandlers.Outline);

        Assert.Contains("Directives", result, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.StartsWith("Error:"), "Unexpected tool-level error: " + result[..Math.Min(200, result.Length)]);
    }

    [Fact]
    public async Task WhenMasterPageProvidedThenReturnsAspxOutline()
    {
        var result = await GetFileOutlineTool.GetFileOutline(filePath: FixturePaths.SiteMasterFile, fmt: new MarkdownFormatter(), handlers: TestHandlers.Outline);

        Assert.Contains("Directives", result, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.StartsWith("Error:"), "Unexpected tool-level error: " + result[..Math.Min(200, result.Length)]);
    }

    [Fact]
    public async Task WhenAspxOutlineProvidedThenContainsExpressions()
    {
        var result = await GetFileOutlineTool.GetFileOutline(filePath: FixturePaths.DefaultAspxFile, fmt: new MarkdownFormatter(), handlers: TestHandlers.Outline);

        Assert.Contains("Expression", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenAspxOutlineProvidedThenContainsCodeBlocks()
    {
        var result = await GetFileOutlineTool.GetFileOutline(filePath: FixturePaths.DefaultAspxFile, fmt: new MarkdownFormatter(), handlers: TestHandlers.Outline);

        // Default.aspx contains <% if (IsPostBack) { %> code blocks
        Assert.Contains("Code Block", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenServicesFileProvidedThenShowsInterfaceAndEnum()
    {
        var result = await GetFileOutlineTool.GetFileOutline(filePath: FixturePaths.ServicesFile, fmt: new MarkdownFormatter());

        Assert.Contains("IStringFormatter", result);
        Assert.Contains("ProcessingStatus", result);
        Assert.Contains("StatisticsCalculator", result);
    }

    [Fact]
    public async Task WhenTextUtilitiesFileProvidedThenShowsMethods()
    {
        var result = await GetFileOutlineTool.GetFileOutline(filePath: FixturePaths.TextUtilitiesFile, fmt: new MarkdownFormatter());

        Assert.Contains("UppercaseFirstCharacter", result);
        Assert.Contains("NormalizeLabel", result);
    }

    [Fact]
    public async Task WhenBrokenSyntaxFileProvidedThenStillProducesOutline()
    {
        var result = await GetFileOutlineTool.GetFileOutline(filePath: FixturePaths.BrokenSyntaxFile, fmt: new MarkdownFormatter());

        // Should still produce some outline even for broken syntax
        Assert.Contains("# Outline:", result);
    }

    [Fact]
    public async Task WhenAsmxFileProvidedThenReturnsOutline()
    {
        var result = await GetFileOutlineTool.GetFileOutline(filePath: FixturePaths.DataServiceFile, fmt: new MarkdownFormatter(), handlers: TestHandlers.Outline);

        Assert.Contains("ASPX File", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenAshxFileProvidedThenReturnsOutline()
    {
        var result = await GetFileOutlineTool.GetFileOutline(filePath: FixturePaths.ImageHandlerFile, fmt: new MarkdownFormatter(), handlers: TestHandlers.Outline);

        Assert.Contains("ASPX File", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenMultipleFilesProvidedThenReturnsAllOutlines()
    {
        var result = await GetFileOutlineTool.GetFileOutline(
            filePath: $"{FixturePaths.CalculatorFile};{FixturePaths.WarningsFile}", fmt: new MarkdownFormatter());

        Assert.Contains("Calculator.cs", result);
        Assert.Contains("Warnings.cs", result);
    }

    [Fact]
    public async Task WhenMethodHasMultilineParametersThenOutlineShowsSingleLine()
    {
        var result = await GetFileOutlineTool.GetFileOutline(filePath: FixturePaths.OutlineShowcaseFile, fmt: new MarkdownFormatter());

        // The method "MultilineParams" has parameters declared across multiple lines in source.
        // After normalization, the outline should show it all on one line.
        Assert.Contains("MultilineParams(", result);
        Assert.DoesNotMatch(@"MultilineParams\(\s*\n", result);
        Assert.Contains("MultilineParams( int firstParam, string secondParam, bool thirdParam)", result);
    }
}

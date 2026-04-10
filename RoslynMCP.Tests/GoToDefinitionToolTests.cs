using RoslynMCP.Services;
using RoslynMCP.Tools;
using Xunit;

namespace RoslynMCP.Tests;

public class GoToDefinitionToolTests
{
    [Fact]
    public async Task WhenEmptyPathProvidedThenReturnsError()
    {
        var result = await GoToDefinitionTool.GoToDefinition(filePath: "", markupSnippet: "[|Add|]", fmt: new MarkdownFormatter());

        Assert.Contains("Error", result);
        Assert.Contains("empty", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenEmptyMarkupProvidedThenReturnsError()
    {
        var result = await GoToDefinitionTool.GoToDefinition(
            filePath: FixturePaths.CalculatorFile, markupSnippet: "", fmt: new MarkdownFormatter());

        Assert.Contains("Error", result);
        Assert.Contains("empty", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenInvalidMarkupProvidedThenReturnsError()
    {
        var result = await GoToDefinitionTool.GoToDefinition(
            filePath: FixturePaths.CalculatorFile, markupSnippet: "no markers here", fmt: new MarkdownFormatter());

        Assert.Contains("Error", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenMethodSymbolTargetedThenReturnsDefinitionLocation()
    {
        var result = await GoToDefinitionTool.GoToDefinition(
            filePath: FixturePaths.CalculatorFile,
            markupSnippet: "return new Result([|Add|](a, b), Subtract(a, b));",
            fmt: new MarkdownFormatter());

        Assert.Contains("Definition: Add", result);
        Assert.Contains("Method", result);
        Assert.Contains("Calculator.cs", result);
    }

    [Fact]
    public async Task WhenTypeSymbolTargetedThenReturnsDefinitionLocation()
    {
        var result = await GoToDefinitionTool.GoToDefinition(
            filePath: FixturePaths.CalculatorFile,
            markupSnippet: "new [|Result|](",
            fmt: new MarkdownFormatter());

        Assert.Contains("Definition: Result", result);
        Assert.Contains("Result.cs", result);
    }

    [Fact]
    public async Task WhenDefinitionFoundThenIncludesCodeContext()
    {
        var result = await GoToDefinitionTool.GoToDefinition(
            filePath: FixturePaths.CalculatorFile,
            markupSnippet: "public int [|Add|](int a, int b)",
            fmt: new MarkdownFormatter());

        Assert.Contains("Source Location", result);
        Assert.Contains("```csharp", result);
    }

    [Fact]
    public async Task WhenSnippetNotFoundThenReturnsNoMatch()
    {
        var result = await GoToDefinitionTool.GoToDefinition(
            filePath: FixturePaths.CalculatorFile,
            markupSnippet: "void [|DoesNotExist|]()",
            fmt: new MarkdownFormatter());

        Assert.Contains("not found", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenFrameworkMethodTargetedThenReturnsMetadataPreview()
    {
        var result = await GoToDefinitionTool.GoToDefinition(
            filePath: FixturePaths.FrameworkReferencesFile,
            markupSnippet: "Console.[|WriteLine|](value);",
            fmt: new MarkdownFormatter());

        Assert.Contains("Decompiled Source", result);
        Assert.Contains("auto-decompiled", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("System.Console", result);
        Assert.Contains("WriteLine", result);
        Assert.Contains("```csharp", result);
    }

    [Fact]
    public async Task WhenFrameworkTypeTargetedThenReturnsDecompiledTypeSource()
    {
        var result = await GoToDefinitionTool.GoToDefinition(
            filePath: FixturePaths.FrameworkReferencesFile,
            markupSnippet: "new [|StringBuilder|]();",
            fmt: new MarkdownFormatter());

        Assert.Contains("Decompiled Source", result);
        Assert.Contains("StringBuilder", result);
        Assert.Contains("System.Text", result);
        Assert.Contains("```csharp", result);
    }

    [Fact]
    public async Task WhenExternalDependencyTargetedThenNestedGoToDefinitionWorks()
    {
        var externalDefinition = await GoToDefinitionTool.GoToDefinition(
            filePath: FixturePaths.ExternalReferencesFile,
            markupSnippet: "return math.[|AddTen|](value);",
            fmt: new MarkdownFormatter());

        Assert.Contains("Definition: AddTen", externalDefinition);
        Assert.Contains("Decompiled Source", externalDefinition);
        Assert.Contains("Aardvark.Empty", externalDefinition);

        string decompiledFilePath = ExtractDefinitionFilePath(externalDefinition);

        var nestedDefinition = await GoToDefinitionTool.GoToDefinition(
            filePath: decompiledFilePath,
            markupSnippet: "return [|ApplyOffset|](value, 10);",
            fmt: new MarkdownFormatter());

        Assert.Contains("Definition: ApplyOffset", nestedDefinition);
        Assert.Contains("Source Location", nestedDefinition);
        Assert.Contains(decompiledFilePath, nestedDefinition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("```csharp", nestedDefinition);
    }

    [Fact]
    public async Task WhenConstructorTargetedThenReturnsContainingTypeName()
    {
        var result = await GoToDefinitionTool.GoToDefinition(
            filePath: FixturePaths.CalculatorFile,
            markupSnippet: "return [|new|] Result(Add(a, b), Subtract(a, b));",
            fmt: new MarkdownFormatter());

        Assert.Contains("Definition: Result", result);
    }

    [Fact]
    public async Task WhenNamespacedTypeTargetedThenNamespaceAppearsInResult()
    {
        var result = await GoToDefinitionTool.GoToDefinition(
            filePath: FixturePaths.FrameworkReferencesFile,
            markupSnippet: "new [|StringBuilder|]();",
            fmt: new MarkdownFormatter());

        Assert.Contains("Namespace", result);
        Assert.Contains("System.Text", result);
    }

    [Fact]
    public async Task WhenFrameworkMethodTargetedThenShowsAssemblyPath()
    {
        var result = await GoToDefinitionTool.GoToDefinition(
            filePath: FixturePaths.FrameworkReferencesFile,
            markupSnippet: "builder.[|Append|](value);",
            fmt: new MarkdownFormatter());

        Assert.Contains("Assembly", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("StringBuilder", result);
    }

    [Fact]
    public async Task WhenPropertyTargetedThenReturnsPropertyDefinition()
    {
        var result = await GoToDefinitionTool.GoToDefinition(
            filePath: FixturePaths.OutlineShowcaseFile,
            markupSnippet: "public string [|Name|] { get;",
            fmt: new MarkdownFormatter());

        Assert.Contains("Definition: Name", result);
        Assert.Contains("Property", result);
    }

    [Fact]
    public async Task WhenEventTargetedThenReturnsEventDefinition()
    {
        var result = await GoToDefinitionTool.GoToDefinition(
            filePath: FixturePaths.OutlineShowcaseFile,
            markupSnippet: "[|Changed|]?.Invoke(this",
            fmt: new MarkdownFormatter());

        Assert.Contains("Definition: Changed", result);
        Assert.Contains("Event", result);
    }

    [Fact]
    public async Task WhenIndexerTargetedThenReturnsIndexerDefinition()
    {
        var result = await GoToDefinitionTool.GoToDefinition(
            filePath: FixturePaths.OutlineShowcaseFile,
            markupSnippet: "public int [|this|][int index]",
            fmt: new MarkdownFormatter());

        // Indexers show as "this[]" or "Item" depending on the symbol
        Assert.Contains("Definition:", result);
        Assert.Contains("Source Location", result);
    }

    [Fact]
    public async Task WhenInterfaceMethodTargetedThenReturnsDefinition()
    {
        var result = await GoToDefinitionTool.GoToDefinition(
            filePath: FixturePaths.ServicesFile,
            markupSnippet: "string [|FormatDisplayValue|](int value);",
            fmt: new MarkdownFormatter());

        Assert.Contains("Definition: FormatDisplayValue", result);
    }

    private static string ExtractDefinitionFilePath(string result)
    {
        const string prefix = "**File**: ";
        string? line = result
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal));

        Assert.False(string.IsNullOrWhiteSpace(line));
        return line![prefix.Length..];
    }

    [Fact]
    public async Task WhenSymbolHasXmlDocsThenShowsSummary()
    {
        // StatisticsCalculator.ComputeAverageSum has XML docs
        var result = await GoToDefinitionTool.GoToDefinition(
            filePath: FixturePaths.ServicesFile,
            markupSnippet: "public double [|ComputeAverageSum|]()",
            fmt: new MarkdownFormatter());

        Assert.Contains("Summary", result);
        Assert.Contains("average sum", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenSymbolHasParamDocsThenShowsSummary()
    {
        // StatisticsCalculator.AddResult has XML docs with summary
        var result = await GoToDefinitionTool.GoToDefinition(
            filePath: FixturePaths.ServicesFile,
            markupSnippet: "public void [|AddResult|](Result result)",
            fmt: new MarkdownFormatter());

        Assert.Contains("Summary", result);
        Assert.Contains("calculation result", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenInterfaceHasXmlDocsThenShowsSummary()
    {
        var result = await GoToDefinitionTool.GoToDefinition(
            filePath: FixturePaths.ServicesFile,
            markupSnippet: "public interface [|IStringFormatter|]",
            fmt: new MarkdownFormatter());

        Assert.Contains("Summary", result);
        Assert.Contains("formatting", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenHintLineProvidedThenDisambiguatesMultipleMatches()
    {
        // "[|Add|]" alone is ambiguous (lines 5 and 11), but hintLine=5 picks the declaration
        var result = await GoToDefinitionTool.GoToDefinition(
            filePath: FixturePaths.CalculatorFile,
            markupSnippet: "[|Add|]",
            fmt: new MarkdownFormatter(),
            hintLine: 5);

        Assert.Contains("Definition: Add", result);
        Assert.DoesNotContain("Ambiguous", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenClassTargetedThenShowsMembersTable()
    {
        var result = await GoToDefinitionTool.GoToDefinition(
            filePath: FixturePaths.CalculatorFile,
            markupSnippet: "public class [|Calculator|]",
            fmt: new MarkdownFormatter());

        Assert.Contains("## Members", result);
        Assert.Contains("Add", result);
        Assert.Contains("Subtract", result);
        Assert.Contains("Compute", result);
        Assert.Contains("method", result);
    }
}

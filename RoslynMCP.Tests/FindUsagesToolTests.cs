using RoslynMCP.Tools;
using Xunit;

namespace RoslynMCP.Tests;

public class FindUsagesToolTests
{
    [Fact]
    public async Task WhenEmptyPathProvidedThenReturnsError()
    {
        var result = await FindUsagesTool.FindUsages(filePath: "", markupSnippet: "[|Add|]");

        Assert.Contains("Error", result);
        Assert.Contains("empty", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenNonExistentFileProvidedThenReturnsError()
    {
        var result = await FindUsagesTool.FindUsages(
            filePath: Path.Combine(FixturePaths.SampleProjectDir, "Ghost.cs"),
            markupSnippet: "[|Add|]");

        Assert.Contains("does not exist", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenEmptyMarkupSnippetProvidedThenReturnsError()
    {
        var result = await FindUsagesTool.FindUsages(
            filePath: FixturePaths.CalculatorFile,
            markupSnippet: "");

        Assert.Contains("Error", result);
        Assert.Contains("empty", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenMarkupSnippetProvidedThenReturnsUsageReport()
    {
        var result = await FindUsagesTool.FindUsages(
            filePath: FixturePaths.CalculatorFile,
            markupSnippet: "public int [|Add|](int a, int b)");

        Assert.Contains("Symbol Usage Analysis", result);
        Assert.Contains("Add", result);
    }

    [Fact]
    public async Task WhenMarkupSnippetForClassUsedThenFindsReferences()
    {
        var result = await FindUsagesTool.FindUsages(
            filePath: FixturePaths.CalculatorFile,
            markupSnippet: "new [|Result|](");

        Assert.Contains("Symbol Usage Analysis", result);
        Assert.Contains("Result", result);
    }

    [Fact]
    public async Task WhenInvalidMarkupProvidedThenReturnsError()
    {
        var result = await FindUsagesTool.FindUsages(
            filePath: FixturePaths.CalculatorFile,
            markupSnippet: "no markers here");

        Assert.Contains("Error", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenSnippetNotFoundInFileThenReturnsNoMatchError()
    {
        var result = await FindUsagesTool.FindUsages(
            filePath: FixturePaths.CalculatorFile,
            markupSnippet: "void [|DoesNotExist|]()");

        Assert.Contains("not found", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenFrameworkMethodTargetedThenFindsProjectReferences()
    {
        var result = await FindUsagesTool.FindUsages(
            filePath: FixturePaths.FrameworkReferencesFile,
            markupSnippet: "Console.[|WriteLine|](value);");

        Assert.Contains("Console.WriteLine", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FrameworkReferences.cs", result);
        Assert.Contains("Reference", result);
    }

    [Fact]
    public async Task WhenUsageReportProducedThenReferencesUsePathLineColumnFormat()
    {
        var result = await FindUsagesTool.FindUsages(
            filePath: FixturePaths.CalculatorFile,
            markupSnippet: "public int [|Add|](int a, int b)");

        Assert.Contains($"{FixturePaths.CalculatorFile}:11:", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("- **Line**:", result, StringComparison.Ordinal);
        Assert.DoesNotContain("- **Column**:", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenManyUsagesExistThenCodeContextIsLimitedToFirstThreeReferences()
    {
        var result = await FindUsagesTool.FindUsages(
            filePath: FixturePaths.CalculatorFile,
            markupSnippet: "public int [|Add|](int a, int b)");

        Assert.Contains(FixturePaths.ManyUsagesFile, result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, CountOccurrences(result, "```csharp"));
        Assert.Contains("Code context shown for the first 3 references", result, StringComparison.Ordinal);
        Assert.Contains("omitted for the remaining", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenPropertyUsagesQueriedThenReturnsPropertyDetails()
    {
        var result = await FindUsagesTool.FindUsages(
            filePath: FixturePaths.OutlineShowcaseFile,
            markupSnippet: "public string [|Name|] { get;");

        Assert.Contains("Symbol Usage Analysis", result);
        Assert.Contains("Name", result);
        Assert.Contains("Property", result);
    }

    [Fact]
    public async Task WhenEventUsagesQueriedThenReturnsEventDetails()
    {
        var result = await FindUsagesTool.FindUsages(
            filePath: FixturePaths.OutlineShowcaseFile,
            markupSnippet: "public event EventHandler? [|Changed|];");

        Assert.Contains("Symbol Usage Analysis", result);
        Assert.Contains("Changed", result);
        Assert.Contains("Event", result);
    }

    [Fact]
    public async Task WhenFieldUsagesQueriedThenReturnsFieldDetails()
    {
        var result = await FindUsagesTool.FindUsages(
            filePath: FixturePaths.OutlineShowcaseFile,
            markupSnippet: "private readonly List<int> [|_values|] = [];");

        Assert.Contains("Symbol Usage Analysis", result);
        Assert.Contains("_values", result);
        Assert.Contains("Field", result);
    }

    [Fact]
    public async Task WhenInterfaceUsagesQueriedThenReturnsUsages()
    {
        var result = await FindUsagesTool.FindUsages(
            filePath: FixturePaths.ServicesFile,
            markupSnippet: "public interface [|IStringFormatter|]");

        Assert.Contains("Symbol Usage Analysis", result);
        Assert.Contains("IStringFormatter", result);
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;

        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}

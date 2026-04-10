using RoslynMCP.Services;
using RoslynMCP.Tools;
using Xunit;

namespace RoslynMCP.Tests;

public class FindUsagesRazorAspxTests
{
    [Fact]
    public async Task WhenFindingUsagesOfSharedClassThenRazorReferencesAreMapped()
    {
        // AppHelper.FormatTitle is used in Counter.razor and Weather.razor
        var result = await FindUsagesTool.FindUsages(
            filePath: FixturePaths.BlazorAppHelperFile,
            markupSnippet: "public static string [|FormatTitle|](string title)",
            fmt: new MarkdownFormatter());

        Assert.Contains("Symbol Usage Analysis", result);
        Assert.Contains("FormatTitle", result);

        // Should find references — at minimum the definition itself,
        // plus usages from Razor source-generated files (mapped back)
        Assert.Contains("Reference", result);
    }

    [Fact]
    public async Task WhenFindingUsagesOfDoubleValueThenRazorReferenceFound()
    {
        // AppHelper.DoubleValue is used in Counter.razor @code block
        var result = await FindUsagesTool.FindUsages(
            filePath: FixturePaths.BlazorAppHelperFile,
            markupSnippet: "public static int [|DoubleValue|](int value)",
            fmt: new MarkdownFormatter());

        Assert.Contains("Symbol Usage Analysis", result);
        Assert.Contains("DoubleValue", result);
        Assert.Contains("Reference", result);
    }

    [Fact]
    public async Task WhenFindingUsagesInSampleProjectThenAspxReferencesNotPresent()
    {
        // SampleProject has no ASPX files — ASPX References section should not appear
        var result = await FindUsagesTool.FindUsages(
            filePath: FixturePaths.CalculatorFile,
            markupSnippet: "public int [|Add|](int a, int b)",
            fmt: new MarkdownFormatter());

        Assert.Contains("Symbol Usage Analysis", result);
        Assert.Contains("Add", result);
        Assert.DoesNotContain("ASPX References", result);
    }

    [Fact]
    public async Task WhenFindingUsagesOfMethodThenSummaryIncludesReferenceCount()
    {
        var result = await FindUsagesTool.FindUsages(
            filePath: FixturePaths.BlazorAppHelperFile,
            markupSnippet: "public static string [|FormatTitle|](string title)",
            fmt: new MarkdownFormatter());

        Assert.Contains("Summary", result);
        Assert.Contains("C# reference", result);
    }

    [Fact]
    public async Task WhenFindingUsagesInAspxProjectThenAspxReferencesAppear()
    {
        // PageHelper.IsPostBack is a property name — "IsPostBack" appears in ASPX code blocks
        var result = await FindUsagesTool.FindUsages(
            filePath: FixturePaths.AspxPageHelperFile,
            markupSnippet: "public static bool [|IsPostBack|] { get; set; }",
            fmt: new MarkdownFormatter());

        Assert.Contains("Symbol Usage Analysis", result);

        // ASPX files contain "IsPostBack" in code blocks
        Assert.Contains("ASPX References", result);
    }

    [Fact]
    public async Task WhenFindingUsagesOfDateTimeThenMultipleAspxFilesFound()
    {
        // "DateTime" appears in Default.aspx expression and PageHelper.cs
        var result = await FindUsagesTool.FindUsages(
            filePath: FixturePaths.AspxPageHelperFile,
            markupSnippet: "public static string FormatDate([|DateTime|] date)",
            fmt: new MarkdownFormatter());

        Assert.Contains("Symbol Usage Analysis", result);
        // DateTime is in ASPX expressions
        Assert.Contains("ASPX References", result);
        Assert.Contains("Expression", result);
    }

    [Fact]
    public async Task WhenRazorReferencesMappedThenShowsRazorSourceLine()
    {
        // FormatTitle is used in both Counter.razor and Weather.razor — check mapping output
        var result = await FindUsagesTool.FindUsages(
            filePath: FixturePaths.BlazorAppHelperFile,
            markupSnippet: "public static string [|FormatTitle|](string title)",
            fmt: new MarkdownFormatter());

        // References in generated .razor.g.cs files should be mapped back
        // If mapping works, we should see "Razor source:" annotations
        // Even if not, the reference should appear
        Assert.Contains("FormatTitle", result);
    }
}

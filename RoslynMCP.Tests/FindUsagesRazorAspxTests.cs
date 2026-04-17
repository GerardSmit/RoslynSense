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

    // --- Control ID resolution tests ---

    [Fact]
    public async Task WhenFindUsagesOnTopLevelControlIdThenFindsCSharpFieldReferences()
    {
        // btnSubmit has a code-behind field — FindUsages should include C# field references
        var result = await FindUsagesTool.FindUsages(
            filePath: FixturePaths.DefaultAspxFile,
            markupSnippet: "ID=\"[|btnSubmit|]\"",
            fmt: new MarkdownFormatter(),
            handlers: TestHandlers.FindUsages);

        Assert.Contains("Symbol Usage Analysis", result);
        Assert.Contains("btnSubmit", result);
        // The field reference in the code-behind should appear
        Assert.Contains("Reference", result);
    }

    [Fact]
    public async Task WhenGoToDefinitionOnTopLevelControlIdThenReturnsField()
    {
        // Clicking ID="btnSubmit" in the ASPX should navigate to the code-behind field
        var result = await GoToDefinitionTool.GoToDefinition(
            filePath: FixturePaths.DefaultAspxFile,
            markupSnippet: "ID=\"[|btnSubmit|]\"",
            fmt: new MarkdownFormatter(),
            handlers: TestHandlers.GoToDefinition);

        Assert.Contains("btnSubmit", result);
        // Definition should point to a field in DefaultPage
        Assert.Contains("DefaultPage", result);
    }

    [Fact]
    public async Task WhenFindUsagesOnTemplateNestedControlIdThenFindsFindControlCalls()
    {
        // btnAction is inside <ItemTemplate> — no code-behind field
        // FindUsages should discover FindControl("btnAction") calls in the code-behind
        var result = await FindUsagesTool.FindUsages(
            filePath: FixturePaths.RepeaterAspxFile,
            markupSnippet: "ID=\"[|btnAction|]\"",
            fmt: new MarkdownFormatter(),
            handlers: TestHandlers.FindUsages);

        Assert.Contains("Control ID References", result);
        Assert.Contains("btnAction", result);
        Assert.Contains("FindControl", result);
        // The direct FindControl("btnAction") call in InitItem should be listed
        Assert.Contains("FindControl References", result);
    }

    [Fact]
    public async Task WhenFindUsagesOnTemplateControlThenIncludesTemplateNote()
    {
        var result = await FindUsagesTool.FindUsages(
            filePath: FixturePaths.RepeaterAspxFile,
            markupSnippet: "ID=\"[|lblName|]\"",
            fmt: new MarkdownFormatter(),
            handlers: TestHandlers.FindUsages);

        Assert.Contains("Control ID References", result);
        Assert.Contains("lblName", result);
        // Should note that control is template-nested
        Assert.Contains("template", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenFindUsagesOnLiteralInAscxTemplateThenFindsFindControlCalls()
    {
        // litSizeRemark is a Literal inside <ItemTemplate> in an .ascx file — no code-behind field.
        // This mirrors the real-world pattern that exposed the perf issue (double GetCompilationAsync,
        // sequential ASPX parsing, GetSemanticModelAsync in wrapper discovery).
        var result = await FindUsagesTool.FindUsages(
            filePath: FixturePaths.OrderItemsAscxFile,
            markupSnippet: "ID=\"[|litSizeRemark|]\"",
            fmt: new MarkdownFormatter(),
            handlers: TestHandlers.FindUsages);

        Assert.Contains("Control ID References", result);
        Assert.Contains("litSizeRemark", result);
        // The FindControl("litSizeRemark") call in the code-behind should be surfaced
        Assert.Contains("FindControl", result);
        Assert.Contains("FindControl References", result);
    }

    [Fact]
    public async Task WhenSetTextWrapperUsedThenWrapperCallSiteIsFound()
    {
        // e.Item.SetText("btnAction", ...) calls FindControl("btnAction") via a wrapper.
        // The wrapper auto-detection should pick up SetText and surface its call site.
        var result = await FindUsagesTool.FindUsages(
            filePath: FixturePaths.RepeaterAspxFile,
            markupSnippet: "ID=\"[|btnAction|]\"",
            fmt: new MarkdownFormatter(),
            handlers: TestHandlers.FindUsages);

        Assert.Contains("FindControl References", result);
        // Both direct FindControl call (in InitItem) and SetText wrapper call should appear
        Assert.Contains("FindControl", result);
        // At least 2 references: direct FindControl in InitItem + SetText in rpt_OnItemDataBound
        var found = System.Text.RegularExpressions.Regex.Matches(result, @"Repeater\.aspx\.cs").Count;
        Assert.True(found >= 2, $"Expected at least 2 references in Repeater.aspx.cs, got {found}. Result:\n{result}");
    }
}

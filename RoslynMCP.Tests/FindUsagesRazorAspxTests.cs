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
        // EventWiring.aspx calls Total() from both <%= %> and <script runat="server">.
        var result = await FindUsagesTool.FindUsages(
            filePath: FixturePaths.EventWiringCodeBehindFile,
            markupSnippet: "protected int [|Total|]() => 42;",
            fmt: new MarkdownFormatter());

        Assert.Contains("Symbol Usage Analysis", result);
        Assert.Contains("ASPX References", result);
    }

    [Fact]
    public async Task WhenAWordIsOnlyMentionedInMarkupThenTheToolDoesNotReportItAsAUsage()
    {
        // EventWiring.aspx names "Total" four times: two real calls, once in a comment and once as a
        // string literal. The tool used to substring-match the symbol name against the markup and
        // reported all four, which is what made rename rewrite words it had no business touching.
        // Asserting on the line numbers the tool prints — rather than on words in the markdown —
        // is what makes this a real check: a text match shows up as extra rows in the table, and
        // "ASPX References" appears either way.
        var result = await FindUsagesTool.FindUsages(
            filePath: FixturePaths.EventWiringCodeBehindFile,
            markupSnippet: "protected int [|Total|]() => 42;",
            fmt: new MarkdownFormatter());

        var reported = AspxReferenceLines(result, "EventWiring.aspx");
        string markup = await File.ReadAllTextAsync(FixturePaths.EventWiringAspxFile);

        // The call inside <script runat="server"> and the one inside <%= %>, and nothing else.
        Assert.Equal(
            new[] { LineOf(markup, "return Total() * 2"), LineOf(markup, "<%= Total()") },
            reported);

        Assert.DoesNotContain(LineOf(markup, "// Total is only mentioned here"), reported);
        Assert.DoesNotContain(LineOf(markup, "string note = \"Total\""), reported);
    }

    [Fact]
    public async Task WhenAMarkupWordBindsElsewhereThenItIsNotReportedAsAUsage()
    {
        // `<% if (IsPostBack) %>` inside a page binds to Page.IsPostBack, not to the same-named
        // property on PageHelper. The old search matched the text and reported it anyway, which is
        // what made rename rewrite words it had no business touching.
        var result = await FindUsagesTool.FindUsages(
            filePath: FixturePaths.AspxPageHelperFile,
            markupSnippet: "public static bool [|IsPostBack|] { get; set; }",
            fmt: new MarkdownFormatter());

        Assert.Contains("Symbol Usage Analysis", result);
        Assert.DoesNotContain("ASPX References", result);
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
        var result = await GoToDefinitionSnippetTool.GoToDefinitionSnippet(
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

    /// <summary>
    /// The line numbers the "ASPX References" table reports for one markup file, in source order.
    /// Reading them out of the table is what lets a test say a reference is absent — asserting on
    /// raw substrings of the markdown cannot tell a real call site from a stray word.
    /// </summary>
    private static List<int> AspxReferenceLines(string markdown, string fileName)
    {
        int start = markdown.IndexOf("## ASPX References", StringComparison.Ordinal);
        Assert.True(start >= 0, $"No ASPX References section in:\n{markdown}");

        int end = markdown.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        string section = end < 0 ? markdown[start..] : markdown[start..end];

        var lines = new List<int>();
        foreach (var row in section.Split('\n'))
        {
            // "| EventWiring.aspx | 12 | ... |" splits into an empty cell, the columns, an empty cell.
            var cells = row.Split('|', StringSplitOptions.TrimEntries);
            if (cells.Length >= 4 && cells[1] == fileName && int.TryParse(cells[2], out int line))
                lines.Add(line);
        }

        lines.Sort();
        return lines;
    }

    /// <summary>
    /// The 1-based line <paramref name="marker"/> sits on, so the expected line numbers come from
    /// the fixture itself and editing the markup cannot quietly invalidate the assertion.
    /// </summary>
    private static int LineOf(string text, string marker)
    {
        int index = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, $"The fixture no longer contains '{marker}'.");
        return text.AsSpan(0, index).Count('\n') + 1;
    }
}

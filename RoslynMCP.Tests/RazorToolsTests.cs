using RoslynMCP.Tools;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Tests for Razor GoToDefinition, File Outline, and Rename support.
/// Uses the BlazorProject fixture (Counter.razor, Weather.razor, AppHelper.cs).
/// </summary>
public class RazorToolsTests
{
    // ── GoToDefinition ──────────────────────────────────────────────

    [Fact]
    public async Task GoToDefinition_RazorInlineExpression_ResolvesToCSharpMethod()
    {
        // @AppHelper.FormatTitle("Counter") — navigate to FormatTitle in AppHelper.cs
        var result = await GoToDefinitionTool.GoToDefinition(
            filePath: FixturePaths.CounterRazorFile,
            markupSnippet: "@AppHelper.[|FormatTitle|](\"Counter\")",
            handlers: TestHandlers.GoToDefinition);

        Assert.Contains("FormatTitle", result);
        Assert.Contains("AppHelper", result);
        // Should show the definition in AppHelper.cs
        Assert.DoesNotContain("Error", result);
    }

    [Fact]
    public async Task GoToDefinition_RazorCodeBlock_ResolvesToCSharpMethod()
    {
        // Inside @code block: AppHelper.DoubleValue(...) — navigate to DoubleValue
        var result = await GoToDefinitionTool.GoToDefinition(
            filePath: FixturePaths.CounterRazorFile,
            markupSnippet: "currentCount = AppHelper.[|DoubleValue|](currentCount + 1)",
            handlers: TestHandlers.GoToDefinition);

        Assert.Contains("DoubleValue", result);
        Assert.Contains("AppHelper", result);
        Assert.DoesNotContain("Error", result);
    }

    [Fact]
    public async Task GoToDefinition_RazorField_ResolvesToFieldInCodeBlock()
    {
        // @currentCount — navigate to field definition in @code block
        var result = await GoToDefinitionTool.GoToDefinition(
            filePath: FixturePaths.CounterRazorFile,
            markupSnippet: "Current count: @[|currentCount|]",
            handlers: TestHandlers.GoToDefinition);

        Assert.Contains("currentCount", result);
        Assert.DoesNotContain("No symbol found", result);
    }

    [Fact]
    public async Task GoToDefinition_WeatherRazor_ResolvesToAppHelper()
    {
        // Weather.razor also uses AppHelper.FormatTitle
        var result = await GoToDefinitionTool.GoToDefinition(
            filePath: FixturePaths.WeatherRazorFile,
            markupSnippet: "@AppHelper.[|FormatTitle|](\"Weather\")",
            handlers: TestHandlers.GoToDefinition);

        Assert.Contains("FormatTitle", result);
        Assert.DoesNotContain("Error", result);
    }

    // ── File Outline ────────────────────────────────────────────────

    [Fact]
    public async Task FileOutline_CounterRazor_ShowsCodeBlockMembers()
    {
        var result = await GetFileOutlineTool.GetFileOutline(
            filePath: FixturePaths.CounterRazorFile,
            handlers: TestHandlers.Outline);

        Assert.Contains("Razor File: Counter.razor", result);
        Assert.Contains("@code Block", result);
        // Should show method and field outlines
        Assert.Contains("IncrementCount", result);
        Assert.Contains("GetCountMessage", result);
    }

    [Fact]
    public async Task FileOutline_WeatherRazor_ShowsDirectivesAndCodeBlock()
    {
        var result = await GetFileOutlineTool.GetFileOutline(
            filePath: FixturePaths.WeatherRazorFile,
            handlers: TestHandlers.Outline);

        Assert.Contains("Razor File: Weather.razor", result);
        Assert.Contains("Directives", result);
        Assert.Contains("@page", result);
        Assert.Contains("/weather", result);
        Assert.Contains("@code Block", result);
        // Should show the inner class and method
        Assert.Contains("OnInitialized", result);
    }

    [Fact]
    public async Task FileOutline_CounterRazor_ShowsInlineExpressions()
    {
        var result = await GetFileOutlineTool.GetFileOutline(
            filePath: FixturePaths.CounterRazorFile,
            handlers: TestHandlers.Outline);

        Assert.Contains("Inline Expressions", result);
    }

    // ── Rename ──────────────────────────────────────────────────────

    [Fact]
    public void ReplaceInDirectives_ReplacesUsing()
    {
        const string text = """
            @using Microsoft.AspNetCore.Components.Web
            @using BlazorProject

            <h1>Hello</h1>
            """;

        var result = RoslynMCP.Tools.Razor.RazorRename.ReplaceInDirectives(text, "BlazorProject", "MyNewNamespace");
        Assert.Contains("@using MyNewNamespace", result);
        Assert.Contains("@using Microsoft.AspNetCore.Components.Web", result);
    }

    [Fact]
    public void ReplaceInDirectives_ReplacesInherits()
    {
        const string text = """
            @page "/test"
            @inherits MyBaseComponent

            <h1>Test</h1>
            """;

        var result = RoslynMCP.Tools.Razor.RazorRename.ReplaceInDirectives(text, "MyBaseComponent", "NewBaseComponent");
        Assert.Contains("@inherits NewBaseComponent", result);
    }

    [Fact]
    public void ReplaceComponentTags_ReplacesOpenAndCloseTags()
    {
        const string text = """
            <MyButton Label="Click me" />
            <MyButton>
                <ChildContent>Inner text</ChildContent>
            </MyButton>
            <NotMyButton />
            """;

        var result = RoslynMCP.Tools.Razor.RazorRename.ReplaceComponentTags(text, "MyButton", "NewButton");
        Assert.Contains("<NewButton Label=\"Click me\" />", result);
        Assert.Contains("<NewButton>", result);
        Assert.Contains("</NewButton>", result);
        Assert.Contains("<NotMyButton />", result); // Different component, untouched
    }

    [Fact]
    public void ReplaceComponentTags_HandlesSelfClosingAndNested()
    {
        const string text = """
            <div>
                <Counter @bind-Value="x" />
                <p>Some text with Counter mentioned</p>
            </div>
            """;

        var result = RoslynMCP.Tools.Razor.RazorRename.ReplaceComponentTags(text, "Counter", "ClickCounter");
        Assert.Contains("<ClickCounter @bind-Value=\"x\" />", result);
        // Text "Counter" in <p> is NOT a tag so it should remain
        Assert.Contains("Some text with Counter mentioned", result);
    }

    [Fact]
    public void ReplaceInDirectives_DoesNotReplaceInNonDirectiveLines()
    {
        const string text = """
            @page "/test"
            <p>@someVar</p>
            @using BlazorProject
            """;

        var result = RoslynMCP.Tools.Razor.RazorRename.ReplaceInDirectives(text, "page", "newpage");
        // @page content ("/test") doesn't contain "page" as a word-boundary match in the value
        Assert.Contains("@page", result);
    }

    [Fact]
    public void ReplaceInDirectives_UsesWordBoundaries()
    {
        const string text = """
            @using MyApp.Services
            @inject MyService myService
            """;

        var result = RoslynMCP.Tools.Razor.RazorRename.ReplaceInDirectives(text, "MyService", "MyNewService");
        Assert.Contains("@inject MyNewService myService", result);
        // Should NOT change "myService" (lowercase) — it has different word boundaries
        Assert.Contains("myService", result);
    }
}

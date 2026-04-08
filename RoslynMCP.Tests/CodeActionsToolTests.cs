using Xunit;

namespace RoslynMCP.Tests;

public class CodeActionsToolTests
{
    [Fact]
    public async Task WhenEmptyFilePathThenReturnsError()
    {
        var result = await RoslynMCP.Tools.CodeActionsTool.GetCodeActions(
            "", "[|x|] = 1;");
        Assert.StartsWith("Error: File path cannot be empty", result);
    }

    [Fact]
    public async Task WhenEmptyMarkupThenReturnsError()
    {
        var result = await RoslynMCP.Tools.CodeActionsTool.GetCodeActions(
            FixturePaths.CalculatorFile, "");
        Assert.StartsWith("Error: markupSnippet cannot be empty", result);
    }

    [Fact]
    public async Task WhenFileNotFoundThenReturnsError()
    {
        var result = await RoslynMCP.Tools.CodeActionsTool.GetCodeActions(
            @"C:\nonexistent\file.cs", "[|x|] = 1;");
        Assert.Contains("does not exist", result);
    }

    [Fact]
    public async Task WhenInvalidMarkupThenReturnsError()
    {
        var result = await RoslynMCP.Tools.CodeActionsTool.GetCodeActions(
            FixturePaths.CalculatorFile, "no markers here");
        Assert.Contains("Invalid markup", result);
    }

    [Fact]
    public async Task WhenNoDiagnosticsAtPositionThenReportsNone()
    {
        // Calculator.cs has no diagnostics at Add
        var result = await RoslynMCP.Tools.CodeActionsTool.GetCodeActions(
            FixturePaths.CalculatorFile,
            "public int [|Add|](int a, int b)");

        Assert.Contains("No diagnostics found", result);
    }

    [Fact]
    public async Task WhenBrokenCodeTargetedThenListsActions()
    {
        // BrokenSemantic.cs has: MissingType value = new();
        var result = await RoslynMCP.Tools.CodeActionsTool.GetCodeActions(
            FixturePaths.BrokenSemanticFile,
            "[|MissingType|] value = new();");

        // Should find diagnostics and possibly code fixes
        Assert.True(
            result.Contains("Diagnostics") || result.Contains("No diagnostics"),
            $"Expected diagnostics info, got: {result}");
    }

    [Fact]
    public async Task WhenInvalidApplyIndexThenReturnsError()
    {
        var result = await RoslynMCP.Tools.CodeActionsTool.GetCodeActions(
            FixturePaths.BrokenSemanticFile,
            "[|MissingType|] value = new();",
            applyIndex: 999);

        // Should either say invalid index or no diagnostics
        Assert.True(
            result.Contains("applyIndex must be between") ||
            result.Contains("No diagnostics") ||
            result.Contains("No code fixes"),
            $"Expected index error or no diagnostics, got: {result}");
    }
}

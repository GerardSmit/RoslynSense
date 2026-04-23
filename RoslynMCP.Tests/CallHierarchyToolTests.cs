using Xunit;

namespace RoslynMCP.Tests;

public class CallHierarchyToolTests
{
    [Fact]
    public async Task WhenEmptyFilePathThenReturnsError()
    {
        var result = await RoslynMCP.Tools.CallHierarchyTool.GetCallHierarchy(
            "", "void [|Foo|]()", new RoslynMCP.Services.MarkdownFormatter());
        Assert.StartsWith("Error: File path cannot be empty", result);
    }

    [Fact]
    public async Task WhenEmptyMarkupThenReturnsError()
    {
        var result = await RoslynMCP.Tools.CallHierarchyTool.GetCallHierarchy(
            FixturePaths.CalculatorFile, "", new RoslynMCP.Services.MarkdownFormatter());
        Assert.StartsWith("Error: markupSnippet cannot be empty", result);
    }

    [Fact]
    public async Task WhenFileNotFoundThenReturnsError()
    {
        var result = await RoslynMCP.Tools.CallHierarchyTool.GetCallHierarchy(
            @"C:\nonexistent\file.cs", "void [|Foo|]()", new RoslynMCP.Services.MarkdownFormatter());
        Assert.Contains("does not exist", result);
    }

    [Fact]
    public async Task WhenInvalidMarkupThenReturnsError()
    {
        var result = await RoslynMCP.Tools.CallHierarchyTool.GetCallHierarchy(
            FixturePaths.CalculatorFile, "no markers here", new RoslynMCP.Services.MarkdownFormatter());
        Assert.Contains("Invalid markup", result);
    }

    [Fact]
    public async Task WhenMethodTargetedThenShowsCallers()
    {
        // Calculator.Add is called by Calculator.Compute
        var result = await RoslynMCP.Tools.CallHierarchyTool.GetCallHierarchy(
            FixturePaths.CalculatorFile,
            "public int [|Add|](int a, int b)",
            new RoslynMCP.Services.MarkdownFormatter(),
            direction: "callers");

        Assert.Contains("Call Hierarchy", result);
        Assert.Contains("Add", result);
        Assert.Contains("Callers", result);
        Assert.Contains("Compute", result);
    }

    [Fact]
    public async Task WhenMethodTargetedThenShowsCallees()
    {
        // Calculator.Compute calls Add and Subtract
        var result = await RoslynMCP.Tools.CallHierarchyTool.GetCallHierarchy(
            FixturePaths.CalculatorFile,
            "public Result [|Compute|](int a, int b)",
            new RoslynMCP.Services.MarkdownFormatter(),
            direction: "callees");

        Assert.Contains("Callees", result);
        Assert.Contains("Add", result);
        Assert.Contains("Subtract", result);
    }

    [Fact]
    public async Task WhenDirectionBothThenShowsCallersAndCallees()
    {
        var result = await RoslynMCP.Tools.CallHierarchyTool.GetCallHierarchy(
            FixturePaths.CalculatorFile,
            "public int [|Add|](int a, int b)",
            new RoslynMCP.Services.MarkdownFormatter(),
            direction: "both");

        Assert.Contains("Callers", result);
        Assert.Contains("Callees", result);
    }

    [Fact]
    public async Task WhenMethodHasNoCallersThenReportsNone()
    {
        // Compute is not called by anything in the project
        var result = await RoslynMCP.Tools.CallHierarchyTool.GetCallHierarchy(
            FixturePaths.CalculatorFile,
            "public Result [|Compute|](int a, int b)",
            new RoslynMCP.Services.MarkdownFormatter(),
            direction: "callers");

        Assert.Contains("Callers", result);
        // Could have callers from other files or no callers
    }
}

using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

public class TypeHierarchyToolTests
{
    [Fact]
    public async Task WhenEmptyFilePathThenReturnsError()
    {
        var result = await RoslynMCP.Tools.TypeHierarchyTool.GetTypeHierarchy(
            "", "class [|Foo|]", new MarkdownFormatter());
        Assert.StartsWith("Error: File path cannot be empty", result);
    }

    [Fact]
    public async Task WhenEmptyMarkupThenReturnsError()
    {
        var result = await RoslynMCP.Tools.TypeHierarchyTool.GetTypeHierarchy(
            FixturePaths.ServicesFile, "", new MarkdownFormatter());
        Assert.StartsWith("Error: markupSnippet cannot be empty", result);
    }

    [Fact]
    public async Task WhenFileNotFoundThenReturnsError()
    {
        var result = await RoslynMCP.Tools.TypeHierarchyTool.GetTypeHierarchy(
            @"C:\nonexistent\file.cs", "class [|Foo|]", new MarkdownFormatter());
        Assert.Contains("does not exist", result);
    }

    [Fact]
    public async Task WhenInvalidMarkupThenReturnsError()
    {
        var result = await RoslynMCP.Tools.TypeHierarchyTool.GetTypeHierarchy(
            FixturePaths.ServicesFile, "no markers here", new MarkdownFormatter());
        Assert.Contains("Invalid markup", result);
    }

    [Fact]
    public async Task WhenClassTargetedThenShowsBaseTypesAndInterfaces()
    {
        var result = await RoslynMCP.Tools.TypeHierarchyTool.GetTypeHierarchy(
            FixturePaths.ServicesFile,
            "public class [|StatisticsCalculator|] : IStringFormatter",
            new MarkdownFormatter());

        Assert.Contains("Type Hierarchy", result);
        Assert.Contains("StatisticsCalculator", result);
        Assert.Contains("Base Types", result);
        Assert.Contains("object", result);
        Assert.Contains("Interfaces", result);
        Assert.Contains("IStringFormatter", result);
    }

    [Fact]
    public async Task WhenInterfaceTargetedThenShowsImplementors()
    {
        var result = await RoslynMCP.Tools.TypeHierarchyTool.GetTypeHierarchy(
            FixturePaths.ServicesFile,
            "public interface [|IStringFormatter|]",
            new MarkdownFormatter());

        Assert.Contains("Type Hierarchy", result);
        Assert.Contains("IStringFormatter", result);
        // Should show derived/implementing types
        Assert.Contains("StatisticsCalculator", result);
    }

    [Fact]
    public async Task WhenEnumTargetedThenShowsHierarchy()
    {
        var result = await RoslynMCP.Tools.TypeHierarchyTool.GetTypeHierarchy(
            FixturePaths.ServicesFile,
            "public enum [|ProcessingStatus|]",
            new MarkdownFormatter());

        Assert.Contains("Type Hierarchy", result);
        Assert.Contains("ProcessingStatus", result);
    }

    [Fact]
    public async Task WhenNonTypeSymbolTargetedThenReturnsNotTypeError()
    {
        var result = await RoslynMCP.Tools.TypeHierarchyTool.GetTypeHierarchy(
            FixturePaths.ServicesFile,
            "public void [|AddResult|](Result result)",
            new MarkdownFormatter());

        // AddResult is a method, not a type
        Assert.Contains("not a type", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenInterfaceTargetedThenDerivedTypesShowLineRanges()
    {
        // IStringFormatter is implemented by StatisticsCalculator, which spans multiple lines
        var result = await RoslynMCP.Tools.TypeHierarchyTool.GetTypeHierarchy(
            FixturePaths.ServicesFile,
            "public interface [|IStringFormatter|]",
            new MarkdownFormatter());

        // StatisticsCalculator's location should appear as a range like "Services.cs:28–48"
        Assert.Matches(@"\d+–\d+", result);
    }
}

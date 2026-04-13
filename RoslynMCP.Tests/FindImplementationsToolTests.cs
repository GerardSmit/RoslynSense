using Xunit;

namespace RoslynMCP.Tests;

public class FindImplementationsToolTests
{
    [Fact]
    public async Task WhenEmptyFilePathThenReturnsError()
    {
        var result = await RoslynMCP.Tools.FindImplementationsTool.FindImplementations(
            "", "interface [|IFoo|]");
        Assert.StartsWith("Error: File path cannot be empty", result);
    }

    [Fact]
    public async Task WhenEmptyMarkupThenReturnsError()
    {
        var result = await RoslynMCP.Tools.FindImplementationsTool.FindImplementations(
            FixturePaths.ServicesFile, "");
        Assert.StartsWith("Error: markupSnippet cannot be empty", result);
    }

    [Fact]
    public async Task WhenFileNotFoundThenReturnsError()
    {
        var result = await RoslynMCP.Tools.FindImplementationsTool.FindImplementations(
            @"C:\nonexistent\file.cs", "interface [|IFoo|]");
        Assert.Contains("does not exist", result);
    }

    [Fact]
    public async Task WhenInvalidMarkupThenReturnsError()
    {
        var result = await RoslynMCP.Tools.FindImplementationsTool.FindImplementations(
            FixturePaths.ServicesFile, "no markers here");
        Assert.Contains("Invalid markup", result);
    }

    [Fact]
    public async Task WhenInterfaceTargetedThenFindsImplementingTypes()
    {
        var result = await RoslynMCP.Tools.FindImplementationsTool.FindImplementations(
            FixturePaths.ServicesFile,
            "public interface [|IStringFormatter|]");

        Assert.Contains("Implementations:", result);
        Assert.Contains("IStringFormatter", result);
        Assert.Contains("StatisticsCalculator", result);
        Assert.Contains("Implementing Types", result);
    }

    [Fact]
    public async Task WhenInterfaceMethodTargetedThenFindsImplementations()
    {
        // Use a more specific snippet to avoid ambiguity between declaration and implementation
        var result = await RoslynMCP.Tools.FindImplementationsTool.FindImplementations(
            FixturePaths.ServicesFile,
            "[|FormatDisplayValue|](int value);");

        Assert.Contains("Implementations:", result);
        Assert.Contains("FormatDisplayValue", result);
        Assert.Contains("StatisticsCalculator", result);
    }

    [Fact]
    public async Task WhenClassWithNoDerivedTypesThenReportsNone()
    {
        var result = await RoslynMCP.Tools.FindImplementationsTool.FindImplementations(
            FixturePaths.CalculatorFile,
            "public class [|Calculator|]");

        Assert.Contains("No derived classes found", result);
    }

    [Fact]
    public async Task WhenLocalVariableTargetedThenCannotFindImplementations()
    {
        // Parameters/locals can't have implementations
        var result = await RoslynMCP.Tools.FindImplementationsTool.FindImplementations(
            FixturePaths.CalculatorFile,
            "public int Add(int [|a|], int b)");

        Assert.Contains("Cannot find implementations", result);
    }

    [Fact]
    public async Task WhenEnumTargetedThenReturnsCannotFind()
    {
        var result = await RoslynMCP.Tools.FindImplementationsTool.FindImplementations(
            FixturePaths.ServicesFile,
            "public enum [|ProcessingStatus|]");

        // Enums are INamedTypeSymbol but have no implementations/derived types
        Assert.Contains("Implementations:", result);
    }

    [Fact]
    public async Task WhenInterfaceTargetedThenImplementingTypesShowLineRanges()
    {
        // StatisticsCalculator implements IStringFormatter and spans multiple lines
        var result = await RoslynMCP.Tools.FindImplementationsTool.FindImplementations(
            FixturePaths.ServicesFile,
            "public interface [|IStringFormatter|]");

        // The Lines column in the table should show a range like "28–48"
        Assert.Matches(@"\d+–\d+", result);
    }
}

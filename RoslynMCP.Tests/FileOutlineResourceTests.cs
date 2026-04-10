using RoslynMCP.Services;
using RoslynMCP.Resources;
using Xunit;

namespace RoslynMCP.Tests;

public class FileOutlineResourceTests
{
    [Fact]
    public async Task WhenEmptyPathProvidedThenReturnsError()
    {
        var result = await FileOutlineResource.GetFileOutlineAsync("", new MarkdownFormatter());

        Assert.Contains("Error", result);
        Assert.Contains("empty", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenNonExistentFileProvidedThenReturnsError()
    {
        var result = await FileOutlineResource.GetFileOutlineAsync("Z:/nonexistent/file.cs", new MarkdownFormatter());

        Assert.Contains("Error", result);
        Assert.Contains("does not exist", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenCalculatorFileProvidedThenReturnsOutline()
    {
        var result = await FileOutlineResource.GetFileOutlineAsync(FixturePaths.CalculatorFile, new MarkdownFormatter());

        Assert.Contains("# Outline:", result);
        Assert.Contains("Calculator.cs", result);
    }

    [Fact]
    public async Task WhenCalculatorFileProvidedThenShowsNamespace()
    {
        var result = await FileOutlineResource.GetFileOutlineAsync(FixturePaths.CalculatorFile, new MarkdownFormatter());

        Assert.Contains("namespace SampleProject", result);
    }

    [Fact]
    public async Task WhenCalculatorFileProvidedThenShowsMethods()
    {
        var result = await FileOutlineResource.GetFileOutlineAsync(FixturePaths.CalculatorFile, new MarkdownFormatter());

        Assert.Contains("Add", result);
        Assert.Contains("Subtract", result);
    }
}

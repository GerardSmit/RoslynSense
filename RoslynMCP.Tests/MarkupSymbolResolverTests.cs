using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

public class MarkupSymbolResolverTests
{
    [Fact]
    public async Task WhenUniqueSnippetMatchesExactlyThenResolverReturnsSymbol()
    {
        var result = await MarkupSymbolResolver.ResolveFromFileAsync(
            FixturePaths.CalculatorFile,
            MarkupString.Parse("public int [|Add|](int a, int b)"));

        Assert.Equal(MarkupResolutionResult.ResultKind.Resolved, result.Kind);
        Assert.Equal("Add", result.Symbol?.Name);
    }

    [Fact]
    public async Task WhenSnippetOnlyMatchesAfterWhitespaceNormalizationThenResolverStillFindsSymbol()
    {
        var result = await MarkupSymbolResolver.ResolveFromFileAsync(
            FixturePaths.CalculatorFile,
            MarkupString.Parse("return new Result(Add(a,\r\n    b), [|Subtract|](a,\r\n    b));"));

        Assert.Equal(MarkupResolutionResult.ResultKind.Resolved, result.Kind);
        Assert.Equal("Subtract", result.Symbol?.Name);
    }

    [Fact]
    public async Task WhenSnippetMatchesMultipleLocationsThenResolverReturnsAmbiguousResult()
    {
        var result = await MarkupSymbolResolver.ResolveFromFileAsync(
            FixturePaths.CalculatorFile,
            MarkupString.Parse("[|Add|]"));

        Assert.Equal(MarkupResolutionResult.ResultKind.Ambiguous, result.Kind);
        Assert.NotNull(result.AmbiguousLineNumbers);
        Assert.True(result.AmbiguousLineNumbers!.Count > 1);
    }

    [Fact]
    public async Task WhenHintLineProvidedForAmbiguousSnippetThenResolvesToNearestMatch()
    {
        // "Add" appears at line 5 (declaration) and line 11 (usage)
        // hintLine=5 should resolve to the declaration
        var result = await MarkupSymbolResolver.ResolveFromFileAsync(
            FixturePaths.CalculatorFile,
            MarkupString.Parse("[|Add|]"),
            hintLine: 5);

        Assert.Equal(MarkupResolutionResult.ResultKind.Resolved, result.Kind);
        Assert.Equal("Add", result.Symbol?.Name);
        Assert.Equal(SymbolKind.Method, result.Symbol?.Kind);
    }

    [Fact]
    public async Task WhenHintLineCloserToSecondMatchThenResolvesToSecondMatch()
    {
        // "Add" appears at line 5 (declaration) and line 11 (usage)
        // hintLine=11 should resolve to the usage at line 11
        var result = await MarkupSymbolResolver.ResolveFromFileAsync(
            FixturePaths.CalculatorFile,
            MarkupString.Parse("[|Add|]"),
            hintLine: 11);

        Assert.Equal(MarkupResolutionResult.ResultKind.Resolved, result.Kind);
        Assert.Equal("Add", result.Symbol?.Name);
    }

    [Fact]
    public async Task WhenHintLineProvidedButOnlyOneMatchThenResolvesNormally()
    {
        // Unique snippet — hintLine should be ignored
        var result = await MarkupSymbolResolver.ResolveFromFileAsync(
            FixturePaths.CalculatorFile,
            MarkupString.Parse("public int [|Add|](int a, int b)"),
            hintLine: 999);

        Assert.Equal(MarkupResolutionResult.ResultKind.Resolved, result.Kind);
        Assert.Equal("Add", result.Symbol?.Name);
    }

    [Fact]
    public async Task WhenSnippetDoesNotAppearInDocumentThenResolverReturnsNoMatch()
    {
        var result = await MarkupSymbolResolver.ResolveFromFileAsync(
            FixturePaths.CalculatorFile,
            MarkupString.Parse("return [|Multiply|](a, b);"));

        Assert.Equal(MarkupResolutionResult.ResultKind.NoMatch, result.Kind);
    }

    [Fact]
    public async Task WhenFileDoesNotExistThenResolverReturnsError()
    {
        var result = await MarkupSymbolResolver.ResolveFromFileAsync(
            Path.Combine(FixturePaths.SampleProjectDir, "MissingFile.cs"),
            MarkupString.Parse("[|Missing|]"));

        Assert.Equal(MarkupResolutionResult.ResultKind.Error, result.Kind);
    }

    [Fact]
    public void WhenNormalizedMatchIsFoundThenMappedOffsetPointsToOriginalFilePosition()
    {
        const string fileText = "return new Result(Add(a, b), Subtract(a, b));";
        const string snippet = "return new Result(Add(a,\r\n    b), Subtract(a,\r\n    b));";

        var matches = MarkupSymbolResolver.FindAllOccurrences(fileText, snippet);

        Assert.Single(matches);
        Assert.False(matches[0].IsExact);

        int snippetOffset = snippet.IndexOf("Subtract", StringComparison.Ordinal);
        int mappedOffset = MarkupSymbolResolver.MapSnippetOffsetToFile(fileText, matches[0], snippet, snippetOffset);

        Assert.Equal(fileText.IndexOf("Subtract", StringComparison.Ordinal), mappedOffset);
    }
}

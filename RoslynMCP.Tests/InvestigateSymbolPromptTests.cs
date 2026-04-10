using RoslynMCP.Prompts;
using Xunit;

namespace RoslynMCP.Tests;

public class InvestigateSymbolPromptTests
{
    [Fact]
    public void WhenParametersProvidedThenReturnsNonEmptyPrompt()
    {
        var prompt = InvestigateSymbolPrompt.InvestigateSymbol(
            filePath: "C:/src/MyFile.cs",
            symbolName: "Calculator");

        Assert.NotEmpty(prompt);
    }

    [Fact]
    public void WhenParametersProvidedThenPromptContainsInvestigationInstructions()
    {
        var prompt = InvestigateSymbolPrompt.InvestigateSymbol(
            filePath: "C:/src/MyFile.cs",
            symbolName: "Calculator");

        Assert.Contains("Please investigate it step by step", prompt);
    }

    [Fact]
    public void WhenParametersProvidedThenPromptContainsSymbolName()
    {
        var prompt = InvestigateSymbolPrompt.InvestigateSymbol(
            filePath: "C:/src/MyFile.cs",
            symbolName: "Calculator");

        Assert.Contains("Calculator", prompt);
    }

    [Fact]
    public void WhenParametersProvidedThenPromptContainsFilePath()
    {
        const string path = "C:/src/MyFile.cs";
        var prompt = InvestigateSymbolPrompt.InvestigateSymbol(
            filePath: path,
            symbolName: "Foo");

        Assert.Contains(path, prompt);
    }

    [Fact]
    public void WhenParametersProvidedThenPromptMentionsSemanticSymbolSearch()
    {
        var prompt = InvestigateSymbolPrompt.InvestigateSymbol(
            filePath: "C:/src/MyFile.cs",
            symbolName: "Foo");

        Assert.Contains("SemanticSymbolSearch", prompt);
    }

    [Fact]
    public void WhenParametersProvidedThenPromptMentionsGoToDefinition()
    {
        var prompt = InvestigateSymbolPrompt.InvestigateSymbol(
            filePath: "C:/src/MyFile.cs",
            symbolName: "Foo");

        Assert.Contains("GoToDefinition", prompt);
    }

    [Fact]
    public void WhenParametersProvidedThenPromptMentionsFindUsages()
    {
        var prompt = InvestigateSymbolPrompt.InvestigateSymbol(
            filePath: "C:/src/MyFile.cs",
            symbolName: "Foo");

        Assert.Contains("FindUsages", prompt);
    }

    [Fact]
    public void WhenNullFilePathProvidedThenThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            InvestigateSymbolPrompt.InvestigateSymbol(filePath: null!, symbolName: "Foo"));
    }

    [Fact]
    public void WhenNullSymbolNameProvidedThenThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            InvestigateSymbolPrompt.InvestigateSymbol(filePath: "C:/src/MyFile.cs", symbolName: null!));
    }
}

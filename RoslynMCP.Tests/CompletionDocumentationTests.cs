using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMCP.Lsp;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Formatting Roslyn's tagged description parts as markdown, which is what the completion
/// popup renders.
/// </summary>
public class CompletionDocumentationTests
{
    private static TaggedText Part(string tag, string text) => new(tag, text);

    [Fact]
    public void TheSignatureBecomesAFencedCSharpBlock()
    {
        var markdown = TaggedTextMarkdown.ToMarkdown(
        [
            Part(TextTags.Keyword, "int"),
            Part(TextTags.Space, " "),
            Part(TextTags.Class, "string"),
            Part(TextTags.Punctuation, "."),
            Part(TextTags.Property, "Length"),
            Part(TextTags.LineBreak, "\n"),
            Part(TextTags.Text, "Gets the number of characters."),
        ]);

        // Fenced so the client highlights it, rather than the one grey run-on line the flattened
        // text produced while still claiming to be markdown.
        Assert.StartsWith("```csharp\nint string.Length\n```", markdown);
        Assert.Contains("Gets the number of characters.", markdown);
    }

    [Fact]
    public void EachDocumentationSectionBecomesItsOwnParagraph()
    {
        var markdown = TaggedTextMarkdown.ToMarkdown(
        [
            Part(TextTags.Method, "Add"),
            Part(TextTags.LineBreak, "\n"),
            Part(TextTags.Text, "Adds two numbers."),
            Part(TextTags.LineBreak, "\n"),
            Part(TextTags.Text, "Returns the sum."),
        ]);

        Assert.Contains("Adds two numbers.\n\nReturns the sum.", markdown);
    }

    [Fact]
    public void ParameterNamesAreCodeSpans()
    {
        var markdown = TaggedTextMarkdown.ToMarkdown(
        [
            Part(TextTags.Method, "Add"),
            Part(TextTags.LineBreak, "\n"),
            Part(TextTags.Text, "Adds "),
            Part(TextTags.Parameter, "left"),
            Part(TextTags.Text, " to it."),
        ]);

        Assert.Contains("Adds `left` to it.", markdown);
    }

    [Fact]
    public void MarkdownInTheDocCommentIsShownRatherThanApplied()
    {
        var markdown = TaggedTextMarkdown.ToMarkdown(
        [
            Part(TextTags.Method, "Glob"),
            Part(TextTags.LineBreak, "\n"),
            Part(TextTags.Text, "Matches *.cs and _internal_ names."),
        ]);

        // Unescaped, the rest of the tooltip would turn italic at the first asterisk.
        Assert.Contains(@"Matches \*.cs and \_internal\_ names.", markdown);
    }

    [Fact]
    public void ADescriptionWithNoDocumentationIsJustTheSignature()
    {
        var markdown = TaggedTextMarkdown.ToMarkdown(
        [
            Part(TextTags.Keyword, "void"),
            Part(TextTags.Space, " "),
            Part(TextTags.Method, "Run"),
        ]);

        Assert.Equal("```csharp\nvoid Run\n```", markdown);
    }

    [Fact]
    public void NothingInMeansNothingOut() =>
        Assert.Equal("", TaggedTextMarkdown.ToMarkdown(ImmutableArray<TaggedText>.Empty));
}

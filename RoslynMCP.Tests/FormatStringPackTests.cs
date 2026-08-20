using Microsoft.CodeAnalysis;
using RoslynMCP.Languages;
using RoslynMCP.Languages.Formatting;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The C# half of the format-string pack: which literals it claims, and what it says about them.
/// </summary>
/// <remarks>
/// Everything here compiles and runs. A specifier is handed to the value's own <c>ToString</c> at
/// run time, so <c>{0:dd-mm-yyyy}</c> is a working program that prints the minute where the month
/// belongs — which is why the answers below are colour and worked examples rather than diagnostics.
/// </remarks>
public class FormatStringPackTests
{
    // ---- What gets claimed ---------------------------------------------------------------------

    [Fact]
    public async Task AFormatCallIsClaimedAsACompositeString() =>
        Assert.Equal("CompositeFormat", await ClaimAsync("\"Completed {0:dd-MM-yyyy} by {1}\""));

    /// <summary>
    /// The format clause of an interpolation is the same language with the value already beside it.
    /// </summary>
    [Fact]
    public async Task AnInterpolationsFormatClauseIsClaimedAsASpecifier() =>
        Assert.Equal("DateTimeFormat", await ClaimAsync("yyyyMMdd"));

    [Fact]
    public async Task AToStringArgumentIsClaimedAsASpecifier() =>
        Assert.Equal("DateTimeFormat", await ClaimAsync("\"HH:mm:ss\""));

    /// <summary>
    /// A string argument that is not a format string is left alone.
    /// </summary>
    /// <remarks>
    /// The claim is the risky half of the pack: it runs against every string literal that is an
    /// argument of a call, and a wrong claim would colour ordinary prose as a date.
    /// </remarks>
    [Fact]
    public async Task APlainArgumentIsNotClaimed() =>
        Assert.Null(await ClaimAsync("\"prefix\""));

    // ---- Hover ---------------------------------------------------------------------------------

    [Fact]
    public async Task HoveringAComponentDescribesItAndWorksAnExample()
    {
        string markdown = await HoverAsync("\"Completed {0:dd-MM-yyyy} by {1}\"", caretIn: "MM");

        Assert.Contains("Month, two digits", markdown, StringComparison.Ordinal);
        Assert.Contains("`dd-MM-yyyy` → `27-03-2026`", markdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// The arithmetic nobody does: a hole binds by position, so which value <c>{1}</c> prints is
    /// only knowable by counting the arguments.
    /// </summary>
    [Fact]
    public async Task HoveringAHoleNamesTheValueItPrints()
    {
        string markdown = await HoverAsync("\"Completed {0:dd-MM-yyyy} by {1}\"", caretIn: "{1}");

        Assert.Contains("string name", markdown, StringComparison.Ordinal);
        Assert.Contains("2nd value", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHoleWithNoSpecifierSaysThereIsNone()
    {
        string markdown = await HoverAsync("\"Ready {0}\"", caretIn: "{0}");

        Assert.Contains("No format specifier", markdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// The value's type decides the grammar, and getting it wrong would describe a decimal as a
    /// date.
    /// </summary>
    [Fact]
    public async Task TheValuesTypeDecidesHowTheSpecifierReads()
    {
        string number = await HoverAsync("\"{0:N2}\"", caretIn: "N2");

        Assert.Contains("Number, with thousands separators, 2 decimal places", number, StringComparison.Ordinal);
        Assert.Contains("`1,234.57`", number, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HoveringInsideAnInterpolationDescribesTheComponent()
    {
        string markdown = await HoverAsync("yyyyMMdd", caretIn: "MM");

        Assert.Contains("Month, two digits", markdown, StringComparison.Ordinal);
        Assert.Contains("`yyyyMMdd` → `20260327`", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HoveringInsideAToStringSpecifierDescribesTheComponent()
    {
        string markdown = await HoverAsync("\"HH:mm:ss\"", caretIn: "mm");

        Assert.Contains("Minute, two digits", markdown, StringComparison.Ordinal);
    }

    // ---- Colour --------------------------------------------------------------------------------

    /// <summary>
    /// The whole reason the pack exists: <c>dd-MM-yyyy</c> and <c>dd-mm-yyyy</c> are one keystroke
    /// apart and one of them is wrong, and three different colours are what makes the pair look
    /// different before anyone reads the letters.
    /// </summary>
    [Fact]
    public async Task TheComponentsOfADateAreColouredApartFromOneAnother()
    {
        var coloured = await TokensAsync("\"Completed {0:dd-MM-yyyy} by {1}\"");

        var used = new[] { "dd", "MM", "yyyy" }.Select(part => coloured[part]);

        Assert.Equal(3, new HashSet<string>(used, StringComparer.Ordinal).Count);
    }

    /// <summary>The literal text between components keeps the colour of the string it is in.</summary>
    [Fact]
    public async Task TheLiteralTextBetweenComponentsIsLeftAlone()
    {
        var coloured = await TokensAsync("\"Completed {0:dd-MM-yyyy} by {1}\"");

        Assert.False(coloured.ContainsKey("-"));
        Assert.False(coloured.ContainsKey("Completed "));
    }

    [Fact]
    public async Task AnInterpolationsComponentsAreColouredWithoutItsBraces()
    {
        var coloured = await TokensAsync("yyyyMMdd");

        Assert.Equal(["yyyy", "MM", "dd"], coloured.Keys.ToArray());
    }

    // ---- The fixture and the harness -----------------------------------------------------------

    private const string Source = """
        using System;

        namespace Application
        {
            public class Report
            {
                public DateTime CompletedDate { get; set; }

                public decimal Total { get; set; }

                public string Lines(string name)
                {
                    return string.Format("Completed {0:dd-MM-yyyy} by {1}", CompletedDate, name)
                        + $"{CompletedDate:yyyyMMdd}"
                        + CompletedDate.ToString("HH:mm:ss")
                        + string.Format("{0:N2}", Total)
                        + string.Format("Ready {0}", name)
                        + name.StartsWith("prefix");
                }
            }
        }
        """;

    private static async Task<string?> ClaimAsync(string anchor)
    {
        var (pack, document, model, token, _, _) = await AtAsync(anchor);
        return await pack.DetectAsync(document, token, model, default);
    }

    private static async Task<string> HoverAsync(string anchor, string caretIn)
    {
        var context = await ContextAsync(anchor, caretIn);
        var hover = await ((IEmbeddedHoverProvider)context.Language).HoverAsync(context, default);

        Assert.NotNull(hover);
        return hover.Contents.Value;
    }

    /// <summary>The coloured runs of a claimed literal, by the text each one covers.</summary>
    private static async Task<Dictionary<string, string>> TokensAsync(string anchor)
    {
        var context = await ContextAsync(anchor, caretIn: null);
        var (_, _, _, _, text, _) = await AtAsync(anchor);

        var tokens = await ((IEmbeddedSemanticTokensProvider)context.Language)
            .SemanticTokensAsync(context, default);

        var coloured = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var token in tokens)
        {
            string covered = text[token.Span.Start..token.Span.End];

            // The punctuation repeats across holes and is not what any of these tests are about.
            if (covered is not ("{" or "}" or ":" or "," or "0" or "1"))
                coloured[covered] = token.TokenType;
        }

        return coloured;
    }

    private static async Task<EmbeddedStringContext> ContextAsync(string anchor, string? caretIn)
    {
        var (pack, document, model, token, text, index) = await AtAsync(anchor);

        string? identifier = await pack.DetectAsync(document, token, model, default);
        Assert.NotNull(identifier);

        int position = token.SpanStart;
        if (caretIn is not null)
        {
            int at = text.IndexOf(caretIn, index, StringComparison.Ordinal);
            Assert.True(at >= 0, $"{caretIn} is not in {anchor}");
            position = at + 1;
        }

        return new EmbeddedStringContext(pack, identifier, [], document, model, token, position);
    }

    private static async Task<(FormattingLanguage Pack, Document Document, SemanticModel Model,
        SyntaxToken Token, string Text, int Index)> AtAsync(string anchor)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();

        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId, VersionStamp.Default, "Application", "Application", LanguageNames.CSharp,
                metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]))
            .AddDocument(DocumentId.CreateNewId(projectId), "Report.cs", Source, filePath: @"C:\src\Report.cs");

        var document = solution.GetProject(projectId)!.Documents.Single();
        string text = (await document.GetTextAsync(default)).ToString();
        var model = await document.GetSemanticModelAsync(default);
        var root = await document.GetSyntaxRootAsync(default);

        int index = text.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(index >= 0, $"{anchor} is not in the fixture");

        return (new FormattingLanguage(), document, model!, root!.FindToken(index + 1), text, index);
    }
}

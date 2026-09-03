using Microsoft.CodeAnalysis;
using RoslynMCP.Languages;
using RoslynMCP.Languages.Formatting;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
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

    // ---- Completion ----------------------------------------------------------------------------

    /// <summary>
    /// The place a list is asked for is the end of what has been typed, which is not a position
    /// the token contains — a caret is a gap between characters and the token is to its left.
    /// </summary>
    [Fact]
    public async Task TheEndOfASpecifierIsInsideIt()
    {
        var offered = await CompletionAsync("$\"{CompletedDate:yyyyMMdd}\"", typed: "yyyyMMdd");

        Assert.Contains("MM", offered.Keys);
        Assert.Contains("dddd", offered.Keys);
    }

    /// <summary>
    /// Having typed a colon and stopped is the strongest signal there is that somebody does not
    /// know what goes after it, and it is the one position that used to answer with nothing: the
    /// compiler leaves an empty specifier zero-width, which nothing finds by looking at a caret.
    /// </summary>
    [Fact]
    public async Task AnEmptySpecifierOffersTheComponentsRatherThanNothing()
    {
        var offered = await CompletionAsync("$\"{Rate:}\"", typed: ":");

        Assert.NotEmpty(offered);
        Assert.Contains("N2", offered.Keys);
    }

    /// <summary>
    /// The value's type picks the list. <c>MM</c> is a month on a date and two literal Ms on a
    /// double, so offering the date components against a number would suggest a specifier that
    /// silently prints its own letters.
    /// </summary>
    [Fact]
    public async Task ADoubleIsOfferedNumberComponentsAndADateIsOfferedDateOnes()
    {
        var number = await CompletionAsync("$\"{Rate:}\"", typed: ":");
        var date = await CompletionAsync("$\"{CompletedDate:yyyyMMdd}\"", typed: "yyyyMMdd");

        Assert.Contains("#,##0.00", number.Keys);
        Assert.DoesNotContain("yyyy", number.Keys);

        Assert.Contains("yyyy", date.Keys);
        Assert.DoesNotContain("#,##0.00", date.Keys);
    }

    /// <summary>
    /// Every one carries what it prints, because the way "is the month MM or mm" is usually
    /// settled is by writing one, running the page and looking.
    /// </summary>
    [Fact]
    public async Task EachComponentIsOfferedWithWhatItPrints()
    {
        var offered = await CompletionAsync("$\"{CompletedDate:yyyyMMdd}\"", typed: "yyyyMMdd");

        Assert.Contains("Month, full name", offered["MMMM"], StringComparison.Ordinal);
        Assert.Contains("March", offered["MMMM"], StringComparison.Ordinal);
    }

    /// <summary>
    /// Mid-specifier, after a separator: the components are what comes next, and the whole list is
    /// offered rather than the ones that have not been used — reading them is half of what it is
    /// for.
    /// </summary>
    [Fact]
    public async Task AfterASeparatorTheComponentsAreOfferedAgain()
    {
        var offered = await CompletionAsync("\"Completed {0:dd-MM-yyyy} by {1}\"", typed: "dd-");

        Assert.Contains("MM", offered.Keys);
        Assert.Contains("dd", offered.Keys);
    }

    /// <summary>
    /// Within the number family, the type still decides: <c>D5</c> compiles against a
    /// <c>double</c> and throws on it at run time, so offering it there is offering a crash.
    /// </summary>
    [Fact]
    public async Task OnlyAWholeNumberIsOfferedTheSpecifiersOnlyItAccepts()
    {
        var whole = await CompletionAsync("$\"{Count:}\"", typed: ":");
        var fractional = await CompletionAsync("$\"{Rate:}\"", typed: ":");

        Assert.Contains("D5", whole.Keys);
        Assert.Contains("X4", whole.Keys);
        Assert.DoesNotContain("D5", fractional.Keys);

        // The two are the same grammar, and the shared list is the bulk of either offer.
        Assert.Contains("N2", whole.Keys);
        Assert.Contains("N2", fractional.Keys);
    }

    /// <summary>
    /// Likewise within the date family. A <c>DateOnly</c> has no time of day and no offset, and
    /// <c>HH</c> on one throws rather than printing a zero.
    /// </summary>
    [Fact]
    public async Task ADateWithNoTimeOfDayIsNotOfferedTheClock()
    {
        var day = await CompletionAsync("$\"{DueDate:}\"", typed: ":");
        var moment = await CompletionAsync("$\"{CompletedDate:yyyyMMdd}\"", typed: "yyyyMMdd");

        Assert.Contains("yyyy", day.Keys);
        Assert.DoesNotContain("HH", day.Keys);
        Assert.DoesNotContain("zzz", day.Keys);

        Assert.Contains("HH", moment.Keys);
    }

    /// <summary>
    /// A caret on the hole's index is choosing which value to print, and the components would be
    /// the wrong list for it. The literal is still a claimed format string — it is the position
    /// inside it that has nothing to say.
    /// </summary>
    [Fact]
    public async Task NothingIsOfferedOutsideASpecifier()
    {
        Assert.Empty(await CompletionAsync("\"Ready {0}\"", typed: "{0"));
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

                public double Rate { get; set; }

                public int Count { get; set; }

                public DateOnly DueDate { get; set; }

                public string Lines(string name)
                {
                    return string.Format("Completed {0:dd-MM-yyyy} by {1}", CompletedDate, name)
                        + $"{CompletedDate:yyyyMMdd}"
                        + CompletedDate.ToString("HH:mm:ss")
                        + string.Format("{0:N2}", Total)
                        + string.Format("Ready {0}", name)
                        + name.StartsWith("prefix");
                }

                // Half-typed, and a compile error until it is finished — which is the state the
                // list exists to get somebody out of.
                public string Half() => $"{Rate:}" + $"{Count:}" + $"{DueDate:}";
            }
        }
        """;

    /// <summary>What is offered with the caret right after <paramref name="typed"/>, by label.</summary>
    private static async Task<Dictionary<string, string>> CompletionAsync(string anchor, string typed)
    {
        var found = await ContextForCompletionAsync(anchor, typed);
        Assert.NotNull(found);

        var context = found.Value;
        var text = await context.Document.GetTextAsync(default);

        var list = await ((IEmbeddedCompletionProvider)context.Language).CompletionAsync(
            context,
            new CompletionParams(
                new TextDocumentIdentifier("file:///C:/src/Report.cs"),
                LspConverters.ToPosition(text.Lines.GetLinePosition(context.Position))),
            default);

        return list.Items.ToDictionary(
            item => item.Label, item => item.Detail ?? "", StringComparer.Ordinal);
    }

    /// <summary>
    /// The context a completion request would run against, through the same detection the handler
    /// uses — which is the half that was broken.
    /// </summary>
    private static async Task<EmbeddedStringContext?> ContextForCompletionAsync(
        string anchor, string typed)
    {
        var (_, document, _, _, text, index) = await AtAsync(anchor);

        int at = text.IndexOf(typed, index, StringComparison.Ordinal);
        Assert.True(at >= 0, $"{typed} is not in {anchor}");

        return await new RoslynEmbeddedLanguages([new FormattingLanguage()])
            .DetectForCompletionAsync(document, at + typed.Length, default);
    }

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

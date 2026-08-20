using RoslynMCP.Languages.Formatting;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The grammar behind the format-string colouring, hovers and worked examples.
/// </summary>
/// <remarks>
/// Pure and workspace-free, because it is one language written in two places — markup's
/// <c>DataFormatString</c> and C#'s interpolated strings — and the whole value of sharing it is
/// that both answer the same way. A test per host would let them drift apart and still pass.
/// </remarks>
public class FormatStringTests
{
    [Fact]
    public void AHoleCarriesItsIndexAndItsSpecifier()
    {
        const string Text = "{0:dd-MM-yyyy}";

        var hole = Assert.Single(FormatString.Holes(Text));

        Assert.Equal(0, hole.Index);
        Assert.Equal(Text, Text[hole.Span.Start..hole.Span.End]);
        Assert.Equal("dd-MM-yyyy", Text[hole.Specifier.Start..hole.Specifier.End]);
    }

    [Fact]
    public void AHoleMayCarryAnAlignmentBeforeItsSpecifier()
    {
        const string Text = "{1,-10:n2}";

        var hole = Assert.Single(FormatString.Holes(Text));

        Assert.Equal(1, hole.Index);
        Assert.Equal("-10", Text[hole.Alignment.Start..hole.Alignment.End]);
        Assert.Equal("n2", Text[hole.Specifier.Start..hole.Specifier.End]);
    }

    /// <summary>An interpolated hole names an expression rather than an index.</summary>
    [Fact]
    public void AnInterpolatedHoleKeepsItsExpression()
    {
        var hole = Assert.Single(FormatString.Holes("{DateTime.Now:yyyyMMdd}"));

        Assert.Equal(-1, hole.Index);
        Assert.Equal("DateTime.Now", hole.Name);
    }

    /// <summary><c>{{</c> is a brace, not a hole.</summary>
    [Fact]
    public void AnEscapedBraceIsNotAHole() =>
        Assert.Empty(FormatString.Holes("{{0}}"));

    /// <summary>
    /// A hole inside a hole does not cut the outer one short.
    /// </summary>
    /// <remarks>
    /// <c>$"{(flag ? $"{x:d}" : "")}"</c> is legal and rare, and stopping at the first closing
    /// brace would give the outer hole a specifier made of half an expression.
    /// </remarks>
    [Fact]
    public void ANestedHoleIsPartOfTheOuterOne()
    {
        const string Text = """{(flag ? $"{x:d}" : "")}""";

        var hole = Assert.Single(FormatString.Holes(Text));

        Assert.Equal(Text, Text[hole.Span.Start..hole.Span.End]);
    }

    /// <summary>The caret picks a hole out of a string that has several.</summary>
    [Fact]
    public void TheHoleAtAnOffsetIsTheOneTheCaretIsIn()
    {
        const string Text = "{0:dd-MM-yyyy} at {1:HH:mm}";
        var holes = FormatString.Holes(Text);

        Assert.Equal(0, FormatString.HoleAt(holes, Text.IndexOf("dd"))?.Index);
        Assert.Equal(1, FormatString.HoleAt(holes, Text.IndexOf("HH"))?.Index);
        Assert.Null(FormatString.HoleAt(holes, Text.IndexOf(" at ") + 2));
    }

    [Fact]
    public void ASpecifierSplitsIntoItsComponents()
    {
        var parts = FormatString.Parts("dd-MM-yyyy");

        Assert.Collection(parts,
            p => Assert.Equal(FormatPartKind.Day, p.Kind),
            p => Assert.Equal(FormatPartKind.Literal, p.Kind),
            p => Assert.Equal(FormatPartKind.Month, p.Kind),
            p => Assert.Equal(FormatPartKind.Literal, p.Kind),
            p => Assert.Equal(FormatPartKind.Year, p.Kind));
    }

    /// <summary>A run of one character is one component, not one per character.</summary>
    [Fact]
    public void ARunOfOneCharacterIsOneComponent()
    {
        var part = Assert.Single(FormatString.Parts("yyyy"));

        Assert.Equal(FormatPartKind.Year, part.Kind);
        Assert.Equal("yyyy", part.Text);
    }

    /// <summary>
    /// Quoted and escaped runs are literal text, however they are spelled.
    /// </summary>
    /// <remarks>
    /// Otherwise the <c>d</c> of a quoted <c>'day'</c> would colour as a day and its hover would
    /// promise a number where the output has a word.
    /// </remarks>
    [Theory]
    [InlineData(@"\d")]
    [InlineData("'day'")]
    public void AnEscapedRunIsLiteral(string specifier)
    {
        var part = Assert.Single(FormatString.Parts(specifier));

        Assert.Equal(FormatPartKind.Escape, part.Kind);
    }

    [Fact]
    public void TheComponentsAreDescribedByWhatTheyProduce()
    {
        Assert.Equal("Month, two digits", FormatString.Describe(Assert.Single(FormatString.Parts("MM"))));
        Assert.Equal("Minute, two digits", FormatString.Describe(Assert.Single(FormatString.Parts("mm"))));
        Assert.Equal("Year, four digits", FormatString.Describe(Assert.Single(FormatString.Parts("yyyy"))));
        Assert.Equal(
            "Hour, 24-hour clock, two digits",
            FormatString.Describe(Assert.Single(FormatString.Parts("HH"))));
    }

    /// <summary>
    /// The example is rendered rather than described, and from a date whose parts cannot be
    /// confused with one another.
    /// </summary>
    [Fact]
    public void AnExampleIsRenderedFromAReadableDate()
    {
        Assert.Equal("27-03-2026", FormatString.Example("dd-MM-yyyy"));
        Assert.Equal("20260327", FormatString.Example("yyyyMMdd"));
        Assert.Equal("14:05:09", FormatString.Example("HH:mm:ss"));
    }

    /// <summary>
    /// A standard specifier is a whole named pattern, not a sequence of components.
    /// </summary>
    /// <remarks>
    /// The runtime's rule, and the reason it has to be honoured: a date specifier is standard only
    /// when it is exactly one character, so <c>{0:d}</c> is the short date pattern while
    /// <c>{0:dd}</c> is a two-digit day. Reading the first as a day would print an example that is
    /// wrong.
    /// </remarks>
    [Fact]
    public void AStandardSpecifierIsOneComponentNamingItsPattern()
    {
        var part = Assert.Single(FormatString.Parts("d"));

        Assert.Equal(FormatPartKind.Standard, part.Kind);
        Assert.Equal("Short date", FormatString.Describe(part));
        Assert.Equal("03/27/2026", FormatString.Example("d"));
    }

    /// <summary>A single character that names no standard pattern is the component it looks
    /// like, rendered the way the runtime asks for it.</summary>
    [Fact]
    public void ALoneCustomSpecifierIsRenderedAsTheComponentItIs()
    {
        Assert.Equal(FormatPartKind.Hour, Assert.Single(FormatString.Parts("H")).Kind);
        Assert.Equal("14", FormatString.Example("H"));
    }

    /// <summary>A specifier the runtime rejects gets no invented example.</summary>
    [Fact]
    public void AnImpossibleSpecifierHasNoExample() =>
        Assert.Null(FormatString.Example("'unterminated"));

    /// <summary>
    /// The value being formatted decides what the letters mean.
    /// </summary>
    /// <remarks>
    /// <c>MM</c> is a two-digit month on a date and two literal Ms on a number. Colouring it as a
    /// month either way would tell the reader a decimal prints a month.
    /// </remarks>
    [Fact]
    public void TheFamilyDecidesWhatALetterMeans()
    {
        Assert.Equal(FormatPartKind.Month, Assert.Single(FormatString.Parts("MM", FormatFamily.Date)).Kind);
        Assert.Equal(FormatPartKind.Literal, Assert.Single(FormatString.Parts("MM", FormatFamily.Number)).Kind);
    }

    [Fact]
    public void ANumericSpecifierIsReadAndRenderedAsANumber()
    {
        var part = Assert.Single(FormatString.Parts("N2", FormatFamily.Number));

        Assert.Equal(FormatPartKind.Standard, part.Kind);
        Assert.Equal("Number, with thousands separators, 2 decimal places",
            FormatString.Describe(part, FormatFamily.Number));
        Assert.Equal("1,234.57", FormatString.Example("N2", FormatFamily.Number));
    }

    [Fact]
    public void ACustomNumericSpecifierSplitsIntoDigitsAndSeparators()
    {
        var kinds = FormatString.Parts("#,##0.00", FormatFamily.Number).Select(p => p.Kind);

        Assert.Equal(
            [
                FormatPartKind.Digit, FormatPartKind.GroupSeparator, FormatPartKind.Digit,
                FormatPartKind.Digit, FormatPartKind.DecimalSeparator, FormatPartKind.Digit,
            ],
            kinds.ToArray());

        Assert.Equal("1,234.57", FormatString.Example("#,##0.00", FormatFamily.Number));
    }

    /// <summary>
    /// A specifier defined for integers only is still worth an example.
    /// </summary>
    /// <remarks>
    /// The reference value has a fraction, which <c>D</c> and <c>X</c> reject; that is a fact
    /// about the reference and not about the specifier the reader wrote.
    /// </remarks>
    [Fact]
    public void AnIntegerOnlySpecifierFallsBackToAnIntegerExample() =>
        Assert.Equal("01234", FormatString.Example("D5", FormatFamily.Number));

    /// <summary>
    /// Nothing named the value, so the letters decide: a specifier with a date component in it is
    /// a date, and one made only of digit placeholders is a number.
    /// </summary>
    [Fact]
    public void AnUnnamedValueIsReadFromWhatTheSpecifierContains()
    {
        Assert.Equal("27-03-2026", FormatString.Example("dd-MM-yyyy"));
        Assert.Equal("1,234.57", FormatString.Example("#,##0.00"));
    }

    /// <summary>The caret picks a component out of the specifier it is in.</summary>
    [Fact]
    public void ThePartAtAnOffsetIsTheOneTheCaretIsIn()
    {
        const string Specifier = "dd-MM-yyyy";
        var parts = FormatString.Parts(Specifier);

        Assert.Equal(FormatPartKind.Month, FormatString.PartAt(parts, Specifier.IndexOf("MM"))?.Kind);
        Assert.Equal(FormatPartKind.Year, FormatString.PartAt(parts, Specifier.Length - 1)?.Kind);
        Assert.Null(FormatString.PartAt(parts, Specifier.Length));
    }
}

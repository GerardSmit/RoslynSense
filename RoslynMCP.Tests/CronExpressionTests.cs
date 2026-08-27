using RoslynMCP.Languages.Cron;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The grammar behind the schedule colouring, hovers and fire times.
/// </summary>
/// <remarks>
/// Pure and workspace-free. One parser reads both dialects, and the reason that is worth doing is
/// that it can then be made to say where they disagree — which is what
/// <see cref="TheSameExpressionNamesADifferentDayInEachDialect"/> pins down. Two parsers written
/// apart would each be self-consistent and the disagreement would go unnoticed, which is exactly
/// how the bug reaches production in the first place.
/// </remarks>
public class CronExpressionTests
{
    /// <summary>
    /// A Friday, and one where no two fields share a value — the day is not the month, and neither
    /// is the hour. A reference date where they collide is one a reader cannot check.
    /// </summary>
    private static readonly DateTime s_now = new(2026, 3, 27, 14, 5, 9);

    private static IReadOnlyList<CronFieldKind> Kinds(string text, CronDialect dialect) =>
        [.. Cron.Parse(text, dialect).Fields.Select(f => f.Kind)];

    private static string Describe(string text, CronDialect dialect) =>
        CronDescription.Describe(Cron.Parse(text, dialect))
        ?? throw new Xunit.Sdk.XunitException($"'{text}' would not read as {dialect}.");

    private static IReadOnlyList<DateTime> Next(string text, CronDialect dialect, int count = 3) =>
        CronDescription.Next(Cron.Parse(text, dialect), s_now, count);

    private static void AssertValid(string text, CronDialect dialect)
    {
        var parse = Cron.Parse(text, dialect);
        Assert.True(
            parse.IsValid,
            $"'{text}' should read as {dialect}: {string.Join("; ", parse.Problems.Select(p => p.Message))}");
    }

    private static CronProblem AssertProblem(string text, CronDialect dialect) =>
        Assert.Single(Cron.Parse(text, dialect).Problems);

    // ---- shape --------------------------------------------------------------------------------

    [Fact]
    public void FiveFieldsStartAtTheMinute()
    {
        Assert.Equal(
            [
                CronFieldKind.Minute, CronFieldKind.Hour, CronFieldKind.DayOfMonth,
                CronFieldKind.Month, CronFieldKind.DayOfWeek,
            ],
            Kinds("0 3 * * *", CronDialect.Hangfire));
    }

    [Fact]
    public void ASixthFieldIsSecondsAndGoesInFront()
    {
        Assert.Equal(CronFieldKind.Second, Kinds("0 0 3 * * *", CronDialect.Hangfire)[0]);
        Assert.Equal(CronFieldKind.Second, Kinds("0 0 3 * * ?", CronDialect.Quartz)[0]);
    }

    /// <summary>Quartz has no five-field form, and a plain crontab has no seven-field one.</summary>
    [Fact]
    public void EachDialectRefusesTheOtherStypicalLength()
    {
        Assert.Contains("six or seven", AssertProblem("0 3 * * *", CronDialect.Quartz).Message);
        Assert.Contains("five or six", AssertProblem("0 0 3 * * ? 2027", CronDialect.Hangfire).Message);
    }

    [Fact]
    public void ASeventhFieldIsTheYear()
    {
        Assert.Equal(CronFieldKind.Year, Kinds("0 0 12 1 1 ? 2027", CronDialect.Quartz)[^1]);
        AssertValid("0 0 12 1 1 ? 2027", CronDialect.Quartz);
    }

    // ---- the divergence -----------------------------------------------------------------------

    /// <summary>
    /// The whole reason the dialect is tracked rather than guessed.
    /// </summary>
    /// <remarks>
    /// Cronos numbers Sunday 0, Quartz numbers it 1, so the same six fields name days a day apart.
    /// Nothing in the string says which, and a reader who has learned one library's numbering will
    /// read the other's wrong every time.
    /// </remarks>
    [Fact]
    public void TheSameExpressionNamesADifferentDayInEachDialect()
    {
        Assert.Contains("Thursday", Describe("0 0 12 ? * 5", CronDialect.Quartz));
        Assert.Contains("Friday", Describe("0 0 12 ? * 5", CronDialect.Hangfire));

        Assert.Equal(DayOfWeek.Thursday, Next("0 0 12 ? * 5", CronDialect.Quartz)[0].DayOfWeek);
        Assert.Equal(DayOfWeek.Friday, Next("0 0 12 ? * 5", CronDialect.Hangfire)[0].DayOfWeek);
    }

    [Fact]
    public void SundayIsSevenAsWellAsZeroOutsideQuartz()
    {
        Assert.Contains("Sunday", Describe("0 22 * * 7", CronDialect.Hangfire));
        Assert.Contains("Sunday", Describe("0 22 * * 0", CronDialect.Hangfire));
        Assert.Contains("Sunday", Describe("0 0 22 ? * 1", CronDialect.Quartz));
    }

    /// <summary>A three-letter name means the same day in both, whatever it is numbered.</summary>
    [Fact]
    public void ANameIsReadThroughTheDialectsNumbering()
    {
        Assert.Equal(
            DayOfWeek.Monday, Next("0 0 3 ? * MON", CronDialect.Quartz)[0].DayOfWeek);
        Assert.Equal(
            DayOfWeek.Monday, Next("0 3 * * MON", CronDialect.Hangfire)[0].DayOfWeek);
    }

    // ---- what each dialect will not accept ----------------------------------------------------

    [Theory]
    [InlineData("60 * * * *", "0 to 59")]
    [InlineData("0 24 * * *", "0 to 23")]
    [InlineData("0 3 32 * *", "1 to 31")]
    [InlineData("0 3 * 13 *", "1 to 12")]
    public void AValueOutsideItsFieldIsReported(string text, string range)
    {
        Assert.Contains(range, AssertProblem(text, CronDialect.Hangfire).Message);
    }

    [Fact]
    public void QuartzHasNoDayZero()
    {
        Assert.Contains("1 to 7", AssertProblem("0 0 12 ? * 0", CronDialect.Quartz).Message);
        AssertValid("0 0 12 ? * 0", CronDialect.Standard);
    }

    /// <summary>Quartz reads one day field and needs the other to say so.</summary>
    [Fact]
    public void QuartzWantsExactlyOneDayFieldToDefer()
    {
        Assert.Contains("only one of the day fields", AssertProblem("0 0 12 1 * 5", CronDialect.Quartz).Message);
        Assert.Contains("neither names a day", AssertProblem("0 0 12 ? * ?", CronDialect.Quartz).Message);

        AssertValid("0 0 12 1 * ?", CronDialect.Quartz);
        AssertValid("0 0 12 ? * 5", CronDialect.Quartz);
    }

    /// <summary>Cronos reads two restricted day fields as "either", so it is no error there.</summary>
    [Fact]
    public void CronosAcceptsBothDayFieldsAtOnce()
    {
        AssertValid("0 3 1 * 5", CronDialect.Hangfire);

        var next = Next("0 3 1 * 5", CronDialect.Hangfire, 4);
        Assert.Contains(next, at => at.Day == 1);
        Assert.Contains(next, at => at.DayOfWeek == DayOfWeek.Friday);
    }

    [Theory]
    [InlineData("0 3 L * *")]
    [InlineData("0 3 LW * *")]
    [InlineData("0 3 15W * *")]
    [InlineData("0 3 * * 5L")]
    [InlineData("0 3 * * 5#3")]
    public void TheCalendarMarkersAreAccepted(string text) =>
        AssertValid(text, CronDialect.Hangfire);

    [Theory]
    [InlineData("0 3 * L *", "end of the month")]
    [InlineData("0 3 * * 5W", "only a day of the month")]
    [InlineData("0 L * * *", "end of the month")]
    [InlineData("0 3 1#2 * *", "counts weeks")]
    [InlineData("0 ? * * *", "no say in")]
    public void AMarkerInAFieldThatHasNoUseForItIsReported(string text, string because)
    {
        Assert.Contains(because, AssertProblem(text, CronDialect.Hangfire).Message);
    }

    [Fact]
    public void AnOrdinalPastTheFifthWeekNeverComes()
    {
        Assert.Contains("never comes", AssertProblem("0 3 * * 5#6", CronDialect.Hangfire).Message);
    }

    [Fact]
    public void AStepLongerThanItsFieldNeverComesRound()
    {
        Assert.Contains("never comes round", AssertProblem("*/61 * * * *", CronDialect.Hangfire).Message);
    }

    [Fact]
    public void AnUnknownMacroIsReportedAgainstTheLibraryThatWouldReadIt()
    {
        Assert.Contains("Hangfire has no macro", AssertProblem("@nope", CronDialect.Hangfire).Message);
    }

    /// <summary>The two Hangfire adds are Hangfire's alone.</summary>
    [Fact]
    public void OnlyHangfireKnowsItsExtraMacros()
    {
        AssertValid("@every_minute", CronDialect.Hangfire);
        Assert.Contains("no macro", AssertProblem("@every_minute", CronDialect.Standard).Message);

        AssertValid("@daily", CronDialect.Standard);
        AssertValid("@daily", CronDialect.Quartz);
    }

    // ---- the sentence -------------------------------------------------------------------------

    [Theory]
    [InlineData("*/10 * * * *", "Every 10 minutes")]
    [InlineData("* * * * *", "Every minute")]
    [InlineData("0 * * * *", "Every hour at :00")]
    [InlineData("30 * * * *", "Every hour at :30")]
    [InlineData("0 3 * * *", "Every day at 03:00")]
    [InlineData("@daily", "Every day at 00:00")]
    [InlineData("0 6,12,18,0 * * *", "Every day at 00:00, 06:00, 12:00 and 18:00")]
    [InlineData("0 22 * * 1-6", "At 22:00, on Monday to Saturday")]
    [InlineData("0 3 L * *", "At 03:00, on the last day")]
    [InlineData("0 3 * * 5#3", "At 03:00, on the 3rd Friday")]
    public void AScheduleReadsAsASentence(string text, string expected) =>
        Assert.Equal(expected, Describe(text, CronDialect.Hangfire));

    // ---- the fire times -----------------------------------------------------------------------

    [Fact]
    public void TheTimesRunForwardFromTheMomentAsked()
    {
        Assert.Equal(
            [
                new DateTime(2026, 3, 27, 14, 10, 0),
                new DateTime(2026, 3, 27, 14, 20, 0),
                new DateTime(2026, 3, 27, 14, 30, 0),
            ],
            Next("*/10 * * * *", CronDialect.Hangfire));
    }

    /// <summary>Strictly after, so a hover taken on the hour says the next one.</summary>
    [Fact]
    public void TheMomentAskedAboutIsNotItselfAnAnswer()
    {
        var parse = Cron.Parse("0 * * * *", CronDialect.Hangfire);
        var onTheHour = new DateTime(2026, 3, 27, 14, 0, 0);

        Assert.Equal(new DateTime(2026, 3, 27, 15, 0, 0), CronDescription.Next(parse, onTheHour, 1)[0]);
    }

    [Fact]
    public void AWeekdayRangeSkipsTheWeekend()
    {
        // The 27th is a Friday, so Saturday follows and Sunday is skipped for Monday the 30th.
        Assert.Equal(
            [
                new DateTime(2026, 3, 27, 22, 0, 0),
                new DateTime(2026, 3, 28, 22, 0, 0),
                new DateTime(2026, 3, 30, 22, 0, 0),
            ],
            Next("0 22 * * 1-6", CronDialect.Hangfire));
    }

    [Fact]
    public void TheLastDayFollowsTheLengthOfEachMonth()
    {
        Assert.Equal(
            [
                new DateTime(2026, 3, 31, 3, 0, 0),
                new DateTime(2026, 4, 30, 3, 0, 0),
                new DateTime(2026, 5, 31, 3, 0, 0),
            ],
            Next("0 3 L * *", CronDialect.Hangfire));
    }

    /// <summary>May 2026 ends on a Sunday, so its last weekday is the Friday before.</summary>
    [Fact]
    public void TheLastWeekdayStepsBackOffAWeekend()
    {
        Assert.Equal(new DateTime(2026, 5, 29, 3, 0, 0), Next("0 3 LW * *", CronDialect.Hangfire)[2]);
    }

    /// <summary>
    /// August 2026 opens on a Saturday, so its "1W" is the Monday — forward, because the weekday
    /// nearest a day is never in the month before it.
    /// </summary>
    [Fact]
    public void ANearestWeekdayNeverCrossesIntoAnotherMonth()
    {
        var parse = Cron.Parse("0 3 1W * *", CronDialect.Hangfire);
        var august = CronDescription.Next(parse, new DateTime(2026, 7, 15), 1);

        Assert.Equal(new DateTime(2026, 8, 3, 3, 0, 0), august[0]);
    }

    [Fact]
    public void AnOrdinalWeekdayLandsInTheRightWeek()
    {
        // April 2026 opens on a Wednesday: its Fridays are the 3rd, 10th, 17th and 24th.
        Assert.Equal(new DateTime(2026, 4, 17, 3, 0, 0), Next("0 3 * * 5#3", CronDialect.Hangfire)[0]);
    }

    /// <summary>A range that runs past the end of its field wraps rather than emptying.</summary>
    [Fact]
    public void AMonthRangeMayWrapTheYear()
    {
        Assert.Equal(new DateTime(2026, 11, 1, 3, 0, 0), Next("0 3 * NOV-FEB *", CronDialect.Hangfire)[0]);

        var parse = Cron.Parse("0 3 * NOV-FEB *", CronDialect.Hangfire);
        Assert.Equal(1, CronDescription.Next(parse, new DateTime(2026, 12, 31, 12, 0, 0), 1)[0].Month);
    }

    [Fact]
    public void AYearNamesTheOnlyTimeItEverComes()
    {
        Assert.Equal([new DateTime(2027, 1, 1, 12, 0, 0)], Next("0 0 12 1 1 ? 2027", CronDialect.Quartz));
    }

    /// <summary>
    /// A day that never arrives gives up rather than looping, and says nothing rather than guessing.
    /// </summary>
    [Fact]
    public void ADayThatNeverComesYieldsNothing()
    {
        AssertValid("0 0 30 2 *", CronDialect.Hangfire);
        Assert.Empty(Next("0 0 30 2 *", CronDialect.Hangfire));
    }

    [Fact]
    public void AnExpressionThatWouldNotReadHasNoFireTimes()
    {
        Assert.Empty(Next("60 * * * *", CronDialect.Hangfire));
        Assert.Null(CronDescription.Describe(Cron.Parse("60 * * * *", CronDialect.Hangfire)));
    }

    // ---- spans --------------------------------------------------------------------------------

    /// <summary>
    /// Every feature but the sentence is built on spans: colouring paints them, hover asks which
    /// one the caret is in, and completion asks which field that span belongs to.
    /// </summary>
    [Fact]
    public void EachFieldKeepsWhereItWasWritten()
    {
        const string Text = "0 6,12 * * 1-5";
        var parse = Cron.Parse(Text, CronDialect.Hangfire);

        Assert.Equal("6,12", Text[parse.Fields[1].Span.Start..parse.Fields[1].Span.End]);
        Assert.Equal("1-5", Text[parse.Fields[4].Span.Start..parse.Fields[4].Span.End]);

        Assert.Equal(CronFieldKind.Hour, parse.FieldAt(Text.IndexOf("12", StringComparison.Ordinal))!.Value.Kind);
        Assert.Equal("12", Text[parse.TermAt(4)!.Value.Span.Start..parse.TermAt(4)!.Value.Span.End]);
    }

    /// <summary>
    /// A field keeps the position just past its last character, which is where completion is asked
    /// for every time — a caret typed onto the end of "1-" is still in the day of week.
    /// </summary>
    /// <remarks>
    /// With the usual single space that leaves no position belonging to nobody, since one field's
    /// inclusive end is the next one's start. Only a wider gap has a middle.
    /// </remarks>
    [Fact]
    public void AFieldKeepsTheCaretAtItsEnd()
    {
        var parse = Cron.Parse("0 3 * * *", CronDialect.Hangfire);
        Assert.Equal(CronFieldKind.Minute, parse.FieldAt(1)!.Value.Kind);
        Assert.Equal(CronFieldKind.Hour, parse.FieldAt(2)!.Value.Kind);

        var spaced = Cron.Parse("0   3 * * *", CronDialect.Hangfire);
        Assert.Equal(CronFieldKind.Minute, spaced.FieldAt(1)!.Value.Kind);
        Assert.Null(spaced.FieldAt(2));
    }
}

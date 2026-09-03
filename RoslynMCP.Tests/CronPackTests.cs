using Microsoft.CodeAnalysis;
using RoslynMCP.Languages;
using RoslynMCP.Languages.Cron;
using RoslynMCP.Languages.Cron.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using Xunit;
using LspDiagnostic = RoslynMCP.Lsp.Protocol.Diagnostic;

namespace RoslynMCP.Tests;

/// <summary>
/// The C# half of the scheduled-job pack: which literals it claims, and what it says about them.
/// </summary>
/// <remarks>
/// Everything here compiles and runs. A crontab expression is handed to a library that reads it on
/// a server months later, so <c>"0 3 1 * *"</c> written where <c>"0 3 * * 1"</c> was meant is a
/// working program that runs monthly instead of weekly — which is why the answers below are colour,
/// sentences and dates rather than compiler errors.
/// </remarks>
public class CronPackTests
{
    // ---- What gets claimed ---------------------------------------------------------------------

    /// <summary>Hangfire's own API, which is the case the shipped table exists for.</summary>
    [Fact]
    public async Task AHangfireRegistrationIsClaimed() =>
        Assert.Equal("Cron", await ClaimAsync("\"*/10 * * * *\""));

    /// <summary>
    /// A method of the solution's own, claimed on the strength of its parameter's name alone.
    /// </summary>
    /// <remarks>
    /// The rule that makes the pack work with nothing configured. A wrapper called
    /// <c>AddJob(string name, string cronExpression, …)</c> is what most solutions actually have,
    /// and it references no scheduling library at all.
    /// </remarks>
    [Fact]
    public async Task AParameterNamedCronExpressionIsClaimedWhoeverDeclaresIt() =>
        Assert.Equal("Cron", await ClaimAsync("\"0 4 * * *\""));

    [Fact]
    public async Task AParameterNamedCronIsClaimedToo() =>
        Assert.Equal("Cron", await ClaimAsync("\"0 5 * * *\""));

    /// <summary>
    /// A string that looks like a schedule but is not one is left alone.
    /// </summary>
    /// <remarks>
    /// The risky half of the pack. The claim runs against every string literal that is an argument
    /// of a call, and a wrong one would colour prose as a schedule and underline it as a broken
    /// one — so a crontab-shaped string passed to a <c>message</c> parameter has to be ignored.
    /// </remarks>
    [Fact]
    public async Task ACrontabShapedStringPassedToSomethingElseIsNotClaimed() =>
        Assert.Null(await ClaimAsync("\"* * * * *\""));

    [Fact]
    public async Task OrdinaryProseIsNotClaimed() =>
        Assert.Null(await ClaimAsync("\"nightly report\""));

    /// <summary>
    /// A removal names a job and carries no schedule, so a crontab-shaped id must not be read as one.
    /// </summary>
    [Fact]
    public async Task ARemovalIsNotClaimedEvenThoughItsArgumentLooksLikeOne() =>
        Assert.Null(await ClaimAsync("\"0 0 1 1 0\""));

    [Fact]
    public async Task AQuartzTriggerIsClaimed() =>
        Assert.Equal("Cron", await ClaimAsync("\"0 0 12 ? * 5\""));

    /// <summary>Nothing is claimed at all when the pack is switched off.</summary>
    [Fact]
    public async Task NothingIsClaimedWhenThePackIsOff() =>
        Assert.Null(await ClaimAsync("\"*/10 * * * *\"", CronSettings.Disabled));

    /// <summary>
    /// A configured binding reaches an in-house scheduler whose parameter is called something
    /// nobody would guess.
    /// </summary>
    [Fact]
    public async Task AConfiguredBindingClaimsAWrapperTheNameRuleWouldMiss()
    {
        Assert.Null(await ClaimAsync("\"0 6 * * *\""));
        Assert.Equal("Cron", await ClaimAsync("\"0 6 * * *\"", Configured()));
    }

    [Fact]
    public async Task AConfiguredBindingNamingATypeThatIsNotThereClaimsNothing()
    {
        var settings = CronSettings.Default with
        {
            Bindings =
            [
                .. CronPresets.Bindings,
                new CronBinding
                {
                    ContainingType = "Nowhere.Scheduler",
                    MemberName = "Enqueue",
                    CronIndex = 1,
                },
            ],
        };

        Assert.Null(await ClaimAsync("\"0 6 * * *\"", settings));
    }

    // ---- Which library reads it -----------------------------------------------------------------

    /// <summary>
    /// The fact a reader cannot recover from the string, and the reason the pack tracks the call
    /// rather than only the text: Hangfire numbers Sunday 0 and Quartz numbers it 1.
    /// </summary>
    [Fact]
    public async Task TheCallDecidesWhichLibrarysNumberingApplies()
    {
        string hangfire = await HoverAsync("\"*/10 * * * *\"");
        string quartz = await HoverAsync("\"0 0 12 ? * 5\"");

        Assert.Contains("Hangfire (Cronos): Sunday is 0", hangfire, StringComparison.Ordinal);
        Assert.Contains("Quartz: seconds first, Sunday is 1", quartz, StringComparison.Ordinal);
    }

    /// <summary>The same six fields, read Quartz's way because a Quartz call handed them over.</summary>
    [Fact]
    public async Task AQuartzScheduleIsDescribedWithQuartzsDays()
    {
        string markdown = await HoverAsync("\"0 0 12 ? * 5\"");

        Assert.Contains("Thursday", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Friday", markdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// The call named no library, so the compilation answers instead: this project references
    /// Hangfire and nothing else, so Hangfire is what reads the wrapper's schedule too.
    /// </summary>
    /// <remarks>
    /// The step that keeps the parameter-name rule from being vague. A wrapper method says nothing
    /// about numbering, and guessing would be as bad as the string it is explaining — but a
    /// solution that references exactly one scheduler has already settled the question.
    /// </remarks>
    [Fact]
    public async Task AWrapperIsReadWithTheOneLibraryTheProjectReferences()
    {
        string markdown = await HoverAsync("\"0 4 * * *\"");

        Assert.Contains("Hangfire (Cronos): Sunday is 0", markdown, StringComparison.Ordinal);
    }

    // ---- Hover ----------------------------------------------------------------------------------

    [Fact]
    public async Task HoveringAScheduleSaysWhatItMeansAndWhenItNextRuns()
    {
        string markdown = await HoverAsync("\"*/10 * * * *\"");

        Assert.Contains("Every 10 minutes", markdown, StringComparison.Ordinal);
        Assert.Contains("Next:", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HoveringAFieldSaysWhichFieldItIsAndWhatItCounts()
    {
        string markdown = await HoverAsync("\"0 22 * * 1-6\"", caretAfter: "* 1");

        Assert.Contains("Day of week", markdown, StringComparison.Ordinal);
        Assert.Contains("Monday (1) to Saturday (6)", markdown, StringComparison.Ordinal);
        Assert.Contains("0 to 7", markdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// The transposition the colouring exists to catch, said out loud: the same digit in two
    /// positions is a weekly job and a monthly one.
    /// </summary>
    [Fact]
    public async Task TheSameDigitMeansADifferentThingInADifferentField()
    {
        string weekly = await HoverAsync("\"0 3 * * 1\"", caretAfter: "* 1");
        string monthly = await HoverAsync("\"0 3 1 * *\"", caretAfter: "3 1");

        Assert.Contains("Day of week", weekly, StringComparison.Ordinal);
        Assert.Contains("Monday (1)", weekly, StringComparison.Ordinal);
        Assert.Contains("Day of month", monthly, StringComparison.Ordinal);
    }

    // ---- Colour ---------------------------------------------------------------------------------

    /// <summary>
    /// The whole reason the pack colours anything: <c>0 3 * * 1</c> and <c>0 3 1 * *</c> are one
    /// transposition apart, and five colours in a fixed order make them two different shapes before
    /// anyone counts the fields.
    /// </summary>
    [Fact]
    public async Task EachFieldOfAScheduleIsColouredApartFromItsNeighbours()
    {
        var tokens = await TokensAsync("\"0 22 * * 1-6\"");

        Assert.Equal(5, tokens.Count);
        Assert.Equal(5, tokens.Select(t => t.Colour).Distinct(StringComparer.Ordinal).Count());

        Assert.Equal("0", tokens[0].Text);
        Assert.Equal("22", tokens[1].Text);
        Assert.Equal("1-6", tokens[4].Text);
    }

    /// <summary>The commas inside one field are punctuation, not more of the field.</summary>
    [Fact]
    public async Task TheCommasOfAListArePaintedAsPunctuation()
    {
        var tokens = await TokensAsync("\"0 6,12,18,0 * * *\"");
        var commas = tokens.Where(t => t.Text == ",").ToList();

        Assert.Equal(3, commas.Count);
        Assert.All(commas, comma => Assert.Equal("operator", comma.Colour));
    }

    /// <summary>A macro stands for a whole expression, so it is coloured as the one word it is.</summary>
    [Fact]
    public async Task AMacroIsColouredAsAKeyword()
    {
        var token = Assert.Single(await TokensAsync("\"@daily\""));

        Assert.Equal("@daily", token.Text);
        Assert.Equal("keyword", token.Colour);
    }

    /// <summary>
    /// A literal that escapes anything maps its offsets to the wrong characters, so it is left the
    /// colour of the string it was.
    /// </summary>
    [Fact]
    public async Task AnEscapedLiteralIsNotColouredAtAll() =>
        Assert.Empty(await TokensAsync("\"0 7 * * *\\u002A\""));

    // ---- Diagnostics ----------------------------------------------------------------------------

    [Fact]
    public async Task AScheduleTheLibraryWouldRejectIsReported()
    {
        var reported = Assert.Single(await DiagnosticsAsync("\"0 99 * * *\""));

        Assert.Equal("CRON0001", reported.Code);
        Assert.Contains("0 to 23", reported.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGoodScheduleIsReportedAsNothing() =>
        Assert.Empty(await DiagnosticsAsync("\"*/10 * * * *\""));

    /// <summary>
    /// Quartz reads six fields where Hangfire reads five, so a five-field expression handed to
    /// Quartz is a job that never loads — and nothing else in the toolchain looks at it.
    /// </summary>
    [Fact]
    public async Task AFiveFieldExpressionHandedToQuartzIsReported()
    {
        var reported = Assert.Single(await DiagnosticsAsync("\"0 8 * * *\""));

        Assert.Contains("six or seven fields", reported.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingIsReportedWhenTheRuleIsOff()
    {
        var settings = CronSettings.Default with { ExpressionDiagnostic = false };

        Assert.Empty(await DiagnosticsAsync("\"0 99 * * *\"", settings));
    }

    // ---- Completion -----------------------------------------------------------------------------

    /// <summary>
    /// The day names beside the numbers, which is the whole answer to "which one is Monday" — a
    /// question normally settled by deploying and waiting a week.
    /// </summary>
    [Fact]
    public async Task TheDayOfWeekFieldOffersTheDaysByName()
    {
        var offered = await CompletionAsync("\"0 22 * * 1-6\"", typed: "* 1");

        Assert.Equal("Monday", offered["1"]);
        Assert.Equal("Sunday", offered["0"]);
        Assert.Contains("5#3", offered.Keys);
    }

    /// <summary>The same position offers months rather than days one field earlier.</summary>
    [Fact]
    public async Task TheMonthFieldOffersTheMonthsByName()
    {
        var offered = await CompletionAsync("\"0 9 * 6 *\"", typed: "* 6");

        Assert.Equal("June", offered["6"]);
        Assert.DoesNotContain("5#3", offered.Keys);
    }

    /// <summary>
    /// An empty literal is where a schedule starts life, and the one place a whole expression is
    /// the useful offer rather than a field of one.
    /// </summary>
    [Fact]
    public async Task AnEmptyScheduleOffersWholeExpressionsAndMacros()
    {
        var offered = await CompletionAsync("x.Run(), \"\");", typed: ", \"");

        Assert.Contains("0 3 * * *", offered.Keys);
        Assert.Contains("@daily", offered.Keys);
        Assert.Contains("every day at 03:00", offered["0 3 * * *"], StringComparison.Ordinal);
    }

    // ---- The fixture and the harness ------------------------------------------------------------

    /// <summary>
    /// Stubs rather than the real packages, so the fixture needs no restore and pins the shapes the
    /// pack keys on rather than whatever version happened to resolve.
    /// </summary>
    /// <remarks>
    /// Hangfire's own <c>AddOrUpdate</c> takes an <c>Expression&lt;Action&lt;T&gt;&gt;</c>. A plain
    /// delegate stands in for it here, because the pack keys on the declaring type, the method name
    /// and the parameter's name — and a stub needing no extra reference cannot fail to bind for a
    /// reason that has nothing to do with what is being tested.
    /// </remarks>
    private const string Source = """
        using System;

        namespace Hangfire
        {
            public sealed class RecurringJobOptions
            {
                public TimeZoneInfo TimeZone { get; set; }
            }

            public static class RecurringJob
            {
                public static void AddOrUpdate<T>(
                    string recurringJobId, Action<T> methodCall, string cronExpression)
                {
                }

                public static void AddOrUpdate<T>(
                    string recurringJobId, Action<T> methodCall, string cronExpression,
                    RecurringJobOptions options)
                {
                }

                public static void RemoveIfExists(string recurringJobId)
                {
                }
            }
        }

        namespace Quartz
        {
            public interface ITriggerConfigurator
            {
                ITriggerConfigurator WithCronSchedule(string cronExpression);
            }
        }

        namespace Application
        {
            using Hangfire;
            using Quartz;

            public sealed class Jobs
            {
                public void Run() { }

                public void Schedule(ITriggerConfigurator trigger)
                {
                    RecurringJob.AddOrUpdate<Jobs>("resend", x => x.Run(), "*/10 * * * *");
                    RecurringJob.AddOrUpdate<Jobs>("weekly", x => x.Run(), "0 22 * * 1-6");
                    RecurringJob.AddOrUpdate<Jobs>("hours", x => x.Run(), "0 6,12,18,0 * * *");
                    RecurringJob.AddOrUpdate<Jobs>("macro", x => x.Run(), "@daily");
                    RecurringJob.AddOrUpdate<Jobs>("escaped", x => x.Run(), "0 7 * * *\u002A");
                    RecurringJob.AddOrUpdate<Jobs>("bad", x => x.Run(), "0 99 * * *");
                    RecurringJob.AddOrUpdate<Jobs>("monday", x => x.Run(), "0 3 * * 1");
                    RecurringJob.AddOrUpdate<Jobs>("first", x => x.Run(), "0 3 1 * *");
                    RecurringJob.AddOrUpdate<Jobs>("june", x => x.Run(), "0 9 * 6 *");
                    RecurringJob.AddOrUpdate<Jobs>("empty", x => x.Run(), "");

                    // Named like a schedule and not one. The id is crontab-shaped on purpose.
                    RecurringJob.RemoveIfExists("0 0 1 1 0");

                    trigger.WithCronSchedule("0 0 12 ? * 5");

                    // Quartz reads six fields, so five handed to it is a schedule that never loads.
                    trigger.WithCronSchedule("0 8 * * *");

                    AddJob("nightly", "0 4 * * *");
                    AtCron("0 5 * * *");
                    Enqueue("wrapped", "0 6 * * *");
                    Announce("* * * * *");
                    Announce("nightly report");
                }

                private void AddJob(string name, string cronExpression) { }

                private void AtCron(string cron) { }

                // Nothing about this says what the second argument is, which is what a configured
                // binding is for.
                private void Enqueue(string name, string when) { }

                private void Announce(string message) { }
            }
        }
        """;

    /// <summary>Settings with a binding for the in-house wrapper the name rule cannot see.</summary>
    private static CronSettings Configured() => CronSettings.Default with
    {
        Bindings =
        [
            .. CronPresets.Bindings,
            new CronBinding
            {
                ContainingType = "Application.Jobs",
                MemberName = "Enqueue",
                CronIndex = 1,
            },
        ],
    };

    private static async Task<string?> ClaimAsync(string anchor, CronSettings? settings = null)
    {
        var (pack, document, model, token, _, _) = await AtAsync(anchor, settings);
        return await pack.DetectAsync(document, token, model, default);
    }

    private static async Task<string> HoverAsync(string anchor, string? caretAfter = null)
    {
        var context = await ContextAsync(anchor, caretAfter);
        var hover = await ((IEmbeddedHoverProvider)context.Language).HoverAsync(context, default);

        Assert.NotNull(hover);
        return hover.Contents.Value;
    }

    /// <summary>The coloured runs of a claimed literal, in the order they were painted.</summary>
    private static async Task<List<(string Text, string Colour)>> TokensAsync(string anchor)
    {
        var context = await ContextAsync(anchor, caretAfter: null);
        var (_, _, _, _, text, _) = await AtAsync(anchor);

        var tokens = await ((IEmbeddedSemanticTokensProvider)context.Language)
            .SemanticTokensAsync(context, default);

        return [.. tokens.Select(t => (text[t.Span.Start..t.Span.End], t.TokenType))];
    }

    private static async Task<IReadOnlyList<LspDiagnostic>> DiagnosticsAsync(
        string anchor, CronSettings? settings = null)
    {
        var context = await ContextAsync(anchor, caretAfter: null, settings);
        return await ((IEmbeddedDiagnosticProvider)context.Language)
            .DiagnosticsAsync(context, default);
    }

    /// <summary>What is offered with the caret right after <paramref name="typed"/>, by label.</summary>
    /// <remarks>
    /// Through the real detection the handler uses rather than a hand-built context: a caret is a
    /// gap between characters and the token is to its left, and that off-by-one is the half of
    /// completion that decides whether an empty literal answers at all.
    /// </remarks>
    private static async Task<Dictionary<string, string>> CompletionAsync(string anchor, string typed)
    {
        var (pack, document, _, _, text, index) = await AtAsync(anchor);

        int at = text.IndexOf(typed, index, StringComparison.Ordinal);
        Assert.True(at >= 0, $"{typed} is not in {anchor}");

        var found = await new RoslynEmbeddedLanguages([pack])
            .DetectForCompletionAsync(document, at + typed.Length, default);

        Assert.NotNull(found);
        var context = found.Value;
        var source = await context.Document.GetTextAsync(default);

        var list = await ((IEmbeddedCompletionProvider)context.Language).CompletionAsync(
            context,
            new CompletionParams(
                new TextDocumentIdentifier("file:///C:/src/Jobs.cs"),
                LspConverters.ToPosition(source.Lines.GetLinePosition(context.Position))),
            default);

        return list.Items.ToDictionary(
            item => item.Label, item => item.Detail ?? "", StringComparer.Ordinal);
    }

    /// <summary>
    /// The context a request would run against, with the caret just past
    /// <paramref name="caretAfter"/> — or on the expression's first character when nothing said
    /// otherwise, since the fields of a schedule sit shoulder to shoulder and no position inside
    /// one belongs to no field.
    /// </summary>
    private static async Task<EmbeddedStringContext> ContextAsync(
        string anchor, string? caretAfter, CronSettings? settings = null)
    {
        var (pack, document, model, token, text, index) = await AtAsync(anchor, settings);

        string? identifier = await pack.DetectAsync(document, token, model, default);
        Assert.NotNull(identifier);

        int position = token.SpanStart + 1;
        if (caretAfter is not null)
        {
            int at = text.IndexOf(caretAfter, index, StringComparison.Ordinal);
            Assert.True(at >= 0, $"{caretAfter} is not in {anchor}");
            position = at + caretAfter.Length;
        }

        return new EmbeddedStringContext(pack, identifier, [], document, model, token, position);
    }

    private static async Task<(CronLanguage Pack, Document Document, SemanticModel Model,
        SyntaxToken Token, string Text, int Index)> AtAsync(
        string anchor, CronSettings? settings = null)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();

        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId, VersionStamp.Default, "Application", "Application", LanguageNames.CSharp,
                metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]))
            .AddDocument(DocumentId.CreateNewId(projectId), "Jobs.cs", Source, filePath: @"C:\src\Jobs.cs");

        var document = solution.GetProject(projectId)!.Documents.Single();
        string text = (await document.GetTextAsync(default)).ToString();
        var model = await document.GetSemanticModelAsync(default);
        var root = await document.GetSyntaxRootAsync(default);

        int index = text.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(index >= 0, $"{anchor} is not in the fixture");

        return (
            new CronLanguage(settings ?? CronSettings.Default),
            document, model!, root!.FindToken(index + 1), text, index);
    }
}

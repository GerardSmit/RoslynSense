using Microsoft.CodeAnalysis;
using RoslynMCP.Languages;
using RoslynMCP.Languages.Logging;
using RoslynMCP.Languages.Logging.Core;
using RoslynMCP.Lsp.Protocol;
using Xunit;
using LspDiagnostic = RoslynMCP.Lsp.Protocol.Diagnostic;
using LspRange = RoslynMCP.Lsp.Protocol.Range;

namespace RoslynMCP.Tests;

/// <summary>
/// What a logging message template means, and the ways it and the values beside it disagree.
/// </summary>
/// <remarks>
/// The pack exists because none of these disagreements is a compile error. Every case below binds,
/// compiles and runs; what it produces is a log line that says the wrong thing, or drops a value
/// that was expensive to compute, or throws away the stack trace of the exception it was reporting.
/// </remarks>
public class LoggingTemplateTests
{
    // ---- The template text on its own ---------------------------------------------------------

    [Fact]
    public void EachHoleIsFoundWithItsNameAndItsPlace()
    {
        var template = MessageTemplate.Parse("Stopped {Name} after {Count} tries.");

        Assert.Equal(["Name", "Count"], template.Holes.Select(hole => hole.Name));
        Assert.Equal([0, 1], template.Holes.Select(hole => hole.Ordinal));
        Assert.Equal("{Name}", "Stopped {Name} after {Count} tries."[template.Holes[0].Span.Start..template.Holes[0].Span.End]);
        Assert.Empty(template.Problems);
    }

    /// <summary>Doubled braces are a literal brace in every one of the four dialects.</summary>
    [Fact]
    public void ADoubledBraceIsTextRatherThanAHole()
    {
        var template = MessageTemplate.Parse("Wrote {{Name}} for {Name}.");

        Assert.Equal(["Name"], template.Holes.Select(hole => hole.Name));
        Assert.Empty(template.Problems);
    }

    /// <summary>
    /// Not a rendering oddity: Microsoft.Extensions.Logging throws <c>FormatException</c> from the
    /// logging call, which is the rare case where a template mistake takes the process with it.
    /// </summary>
    [Fact]
    public void AnUnclosedBraceIsAProblem()
    {
        var template = MessageTemplate.Parse("Stopped {Name after a while.");

        Assert.Empty(template.Holes);
        Assert.Contains("Unclosed", Assert.Single(template.Problems).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AlignmentAndFormatAreSplitOffTheName()
    {
        var hole = Assert.Single(MessageTemplate.Parse("{Count,-6:000}").Holes);

        Assert.Equal("Count", hole.Name);
        Assert.Equal("-6", hole.Alignment);
        Assert.Equal("000", hole.Format);
        Assert.Equal(new Microsoft.CodeAnalysis.Text.TextSpan(1, 5), hole.NameSpan);
    }

    [Theory]
    [InlineData("{@Order}", "Destructure")]
    [InlineData("{$Order}", "Stringify")]
    [InlineData("{Order}", "None")]
    public void TheCaptureOperatorIsReadRatherThanTakenForPartOfTheName(string text, string expected)
    {
        var hole = Assert.Single(MessageTemplate.Parse(text).Holes);

        Assert.Equal("Order", hole.Name);
        Assert.Equal(expected, hole.Hint.ToString());
    }

    /// <summary>
    /// A numbered template binds by the number, so <c>"{1} {0}"</c> is not two values in the order
    /// they were passed and its value count is not its hole count.
    /// </summary>
    [Fact]
    public void ANumberedTemplateBindsByItsNumbersRatherThanByOrder()
    {
        var template = MessageTemplate.Parse("{1} then {1} then {0}");

        Assert.True(template.IsPositional);
        Assert.Equal(2, template.ValueCount);
        Assert.Equal(3, template.Holes.Length);
    }

    /// <summary>Prose with a brace in it is prose, not a hole nothing renders.</summary>
    [Fact]
    public void SomethingThatIsNotANameIsNotAHole()
    {
        Assert.Empty(MessageTemplate.Parse("Try {a b} or {x.y}").Holes);
    }

    // ---- A call, where holes bind by position -------------------------------------------------

    [Fact]
    public async Task TheValuesAndThePlaceholdersHaveToBeTheSameCount()
    {
        var found = await DiagnosticsAsync("\"Stopped {Name}.\"");

        Assert.Equal("LOG0003", Assert.Single(found).Code);
        Assert.Contains("1 placeholder and the call passes 2 values", found[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMatchingCallIsQuiet()
    {
        Assert.Empty(await DiagnosticsAsync("\"Stopped {Name} after {Count} tries.\""));
    }

    /// <summary>
    /// The one the user's own code was doing: the exception handed to the template instead of to
    /// the logger. It compiles, it logs, and the stack trace is gone.
    /// </summary>
    [Fact]
    public async Task AnExceptionRenderedAsAValueIsReported()
    {
        var found = await DiagnosticsAsync("\"Stopped {Name}: {Failure}\"");

        Assert.Equal("LOG0005", Assert.Single(found).Code);
        Assert.Contains("first argument", found[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExceptionInItsProperPlaceIsQuiet()
    {
        Assert.Empty(await DiagnosticsAsync("\"Failed at {Name}.\""));
    }

    [Fact]
    public async Task ATemplateWithNoHolesAndNoValuesIsQuiet()
    {
        Assert.Empty(await DiagnosticsAsync("\"Nothing here at all.\""));
    }

    // ---- The other two libraries ---------------------------------------------------------------

    [Fact]
    public async Task SerilogIsClaimedAndCounted()
    {
        var found = await DiagnosticsAsync("\"{User} left {Room}.\"");

        Assert.Equal("LOG0003", Assert.Single(found).Code);
    }

    [Fact]
    public async Task NLogIsClaimedAndCounted()
    {
        var found = await DiagnosticsAsync("\"{User} went home.\"");

        Assert.Equal(["LOG0003", "LOG0005"], found.Select(d => d.Code).Order());
    }

    // ---- A generated method, where holes bind by name ------------------------------------------

    [Fact]
    public async Task APlaceholderNamingNoParameterIsReported()
    {
        var found = await DiagnosticsAsync("\"{Missing} went wrong.\"");

        Assert.Contains(found, d => d.Code == "LOG0002" && d.Message.Contains("'Missing'", StringComparison.Ordinal));
    }

    /// <summary>
    /// Reported on the parameter rather than on the method, which is the whole reason this restates
    /// what SYSLIB1015 already says: "somewhere in this signature" is not an answer when the
    /// signature has six parameters.
    /// </summary>
    [Fact]
    public async Task AParameterNoPlaceholderRendersIsReportedOnTheParameter()
    {
        var (found, text) = await DiagnosticsWithTextAsync("\"{TypeFullName}.ApplicationEnd threw.\"");

        var unused = Assert.Single(found, d => d.Code == "LOG0004");

        Assert.Contains("'attempt'", unused.Message, StringComparison.Ordinal);
        Assert.Equal("attempt", At(text, unused.Range));
    }

    /// <summary>
    /// The logger and the exception are what the generator consumes itself. Reporting them is the
    /// first thing that would get the rule switched off, so it is a test rather than a comment.
    /// </summary>
    [Fact]
    public async Task TheLoggerAndTheExceptionAreNeverReportedAsUnused()
    {
        var found = await DiagnosticsAsync("\"{TypeFullName}.ApplicationEnd threw.\"");

        Assert.DoesNotContain(found, d => d.Message.Contains("logger", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(found, d => d.Message.Contains("exception", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ARuleThatIsSwitchedOffSaysNothing()
    {
        var settings = new LoggingSettings(
            Enabled: true, UnknownPlaceholder: false, UnusedValue: false, ValueCount: false,
            ExceptionPosition: false, TemplateSyntax: false);

        Assert.Empty(await DiagnosticsAsync("\"Stopped {Name}.\"", settings));
    }

    // ---- Hover ----------------------------------------------------------------------------------

    [Fact]
    public async Task HoverOnAHoleNamesTheValueItPrintsAndSaysHowItGotThere()
    {
        var hover = await HoverAsync("\"Stopped {Name} after {Count} tries.\"", "{Count}");

        Assert.NotNull(hover);
        Assert.Contains("int count", hover!.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("2nd value", hover.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("by position, not by name", hover.Contents.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HoverOnAGeneratedMethodSHoleSaysItIsMatchedByName()
    {
        var hover = await HoverAsync("\"{TypeFullName}.ApplicationEnd threw.\"", "{TypeFullName}");

        Assert.NotNull(hover);
        Assert.Contains("string typeFullName", hover!.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("by name", hover.Contents.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HoverExplainsTheCaptureOperatorAndTheFormat()
    {
        var hover = await HoverAsync(
            "\"Padded {Count,6:000} and {@Name} and {$Name}.\"", "{Count,6:000}");

        Assert.NotNull(hover);
        Assert.Contains("Padded to 6 characters", hover!.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("Formatted with `000`", hover.Contents.Value, StringComparison.Ordinal);
    }

    // ---- Completion ------------------------------------------------------------------------------

    [Fact]
    public async Task CompletionOffersWhatTheCallPassesInTheSpellingATemplateUses()
    {
        var items = await CompletionAsync("\"Stopped {Name} after {Count} tries.\"", "{Name");

        Assert.Equal(["Count", "Name"], items.Select(item => item.Label).Order());

        // Names as a log property is written, not as the parameter is: the source generator matches
        // case-insensitively and every sink shows the property, so PascalCase is the useful answer.
        Assert.Contains(items, item => item.Detail == "int count" && item.Label == "Count");
    }

    /// <summary>Under positional binding exactly one of the offered names can be right here.</summary>
    [Fact]
    public async Task TheValueThisPositionActuallyRendersIsOfferedFirst()
    {
        var items = await CompletionAsync("\"Stopped {Name} after {Count} tries.\"", "{Count");

        var first = items.OrderBy(item => item.SortText, StringComparer.Ordinal).First();

        Assert.Equal("Count", first.Label);
        Assert.True(first.Preselect);
    }

    // ---- Colour -----------------------------------------------------------------------------------

    [Fact]
    public async Task TheBracesAndTheBoundNameAreColoured()
    {
        var (tokens, text) = await TokensAsync("\"Stopped {Name} after {Count} tries.\"");

        Assert.Equal(
            ["{", "Name", "}", "{", "Count", "}"],
            tokens.Select(token => text.Substring(token.Span.Start, token.Span.Length)));

        Assert.Equal(
            ["operator", "parameter", "operator", "operator", "parameter", "operator"],
            tokens.Select(token => token.TokenType));
    }

    /// <summary>
    /// A hole that binds nothing prints as the literal text <c>{Missing}</c>, so it is left the
    /// colour of the string it is printed as. The squiggle says the rest.
    /// </summary>
    [Fact]
    public async Task AHoleThatBindsNothingKeepsTheColourOfTheStringAroundIt()
    {
        var (tokens, text) = await TokensAsync("\"{Missing} went wrong.\"");

        Assert.DoesNotContain(
            tokens, token => text.Substring(token.Span.Start, token.Span.Length) == "Missing");
    }

    // ---- Building the pieces ----------------------------------------------------------------------

    /// <summary>
    /// Stubs rather than the real packages: what the pack matches on is the namespace, the parameter
    /// name and the shape of the overload, so a faithful stub exercises exactly the same path and
    /// the test project stays free of three logging dependencies.
    /// </summary>
    private const string Source = """
        using System;

        namespace Microsoft.Extensions.Logging
        {
            public interface ILogger { }

            public enum LogLevel { Trace, Debug, Information, Warning, Error, Critical, None }

            public static class LoggerExtensions
            {
                public static void LogWarning(this ILogger logger, string message, params object[] args) { }

                public static void LogWarning(this ILogger logger, Exception exception, string message, params object[] args) { }

                public static void LogError(this ILogger logger, Exception exception, string message, params object[] args) { }
            }

            [AttributeUsage(AttributeTargets.Method)]
            public sealed class LoggerMessageAttribute : Attribute
            {
                public int EventId { get; set; }
                public LogLevel Level { get; set; }
                public string Message { get; set; }
            }
        }

        namespace Serilog
        {
            public interface ILogger
            {
                void Warning(string messageTemplate, params object[] propertyValues);
            }
        }

        namespace NLog
        {
            public interface ILogger
            {
                void Warn(string message, params object[] args);
            }
        }

        namespace Contoso.App
        {
            using Microsoft.Extensions.Logging;

            public class Shutdown
            {
                private readonly ILogger _logger;

                public Shutdown(ILogger logger) => _logger = logger;

                public void Calls(string name, int count, Exception failure)
                {
                    _logger.LogWarning("Stopped {Name} after {Count} tries.", name, count);
                    _logger.LogWarning("Stopped {Name}.", name, count);
                    _logger.LogWarning("Stopped {Name}: {Failure}", name, failure);
                    _logger.LogError(failure, "Failed at {Name}.", name);
                    _logger.LogWarning("Nothing here at all.");
                    _logger.LogWarning("Padded {Count,6:000} and {@Name} and {$Name}.", count, name, name);
                }

                // Written without `partial` because the source generator is not running here and an
                // unimplemented partial method is a compile error. Nothing in the pack reads the
                // modifier; what it reads is the attribute and the parameter list.
                [LoggerMessage(EventId = 5001, Level = LogLevel.Error, Message = "{TypeFullName}.ApplicationEnd threw.")]
                public void ApplicationEndFailed(ILogger logger, Exception exception, string typeFullName, int attempt) { }

                [LoggerMessage(EventId = 5002, Level = LogLevel.Error, Message = "{Missing} went wrong.")]
                public void Unmatched(string typeFullName) { }
            }

            public class Others
            {
                public void Write(Serilog.ILogger serilog, NLog.ILogger nlog, string user, Exception failure)
                {
                    serilog.Warning("{User} left {Room}.", user);
                    nlog.Warn("{User} went home.", user, failure);
                }
            }
        }
        """;

    private static LoggingSettings AllRules { get; } =
        new(Enabled: true, UnknownPlaceholder: true, UnusedValue: true, ValueCount: true,
            ExceptionPosition: true, TemplateSyntax: true);

    private static async Task<IReadOnlyList<LspDiagnostic>> DiagnosticsAsync(
        string literal, LoggingSettings? settings = null)
    {
        var (found, _) = await DiagnosticsWithTextAsync(literal, settings);
        return found;
    }

    private static async Task<(IReadOnlyList<LspDiagnostic> Found, string Text)> DiagnosticsWithTextAsync(
        string literal, LoggingSettings? settings = null)
    {
        var (pack, context, text) = await EmbeddedAsync(literal, settings);
        return (await pack.DiagnosticsAsync(context, default), text);
    }

    private static async Task<Hover?> HoverAsync(string literal, string hole)
    {
        var (pack, context, text) = await EmbeddedAsync(literal, caretIn: hole);
        _ = text;
        return await pack.HoverAsync(context, default);
    }

    private static async Task<IReadOnlyList<CompletionItem>> CompletionAsync(string literal, string upTo)
    {
        var (pack, context, text) = await EmbeddedAsync(literal, caretAfter: upTo);
        _ = text;

        var list = await pack.CompletionAsync(
            context,
            new CompletionParams(
                new TextDocumentIdentifier(""), new Position(0, 0), null),
            default);

        return list.Items;
    }

    private static async Task<(IReadOnlyList<EmbeddedToken> Tokens, string Text)> TokensAsync(string literal)
    {
        var (pack, context, text) = await EmbeddedAsync(literal);
        return (await pack.SemanticTokensAsync(context, default), text);
    }

    /// <summary>
    /// The pack and a context over the literal, built the way the embedded detector builds one —
    /// through <c>DetectAsync</c>, so every test covers the claim as well as the answer.
    /// </summary>
    private static async Task<(LoggingLanguage Pack, EmbeddedStringContext Context, string Text)> EmbeddedAsync(
        string literal, LoggingSettings? settings = null, string? caretIn = null, string? caretAfter = null)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();

        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId, VersionStamp.Default, "Application", "Application", LanguageNames.CSharp,
                metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]))
            .AddDocument(DocumentId.CreateNewId(projectId), "Logging.cs", Source, filePath: @"C:\src\Logging.cs");

        var document = solution.GetProject(projectId)!.Documents.Single();
        var text = (await document.GetTextAsync(default)).ToString();
        var model = await document.GetSemanticModelAsync(default);
        var root = await document.GetSyntaxRootAsync(default);

        int index = text.IndexOf(literal, StringComparison.Ordinal);
        Assert.True(index >= 0, $"{literal} is not in the fixture");

        var token = root!.FindToken(index + 1);
        var pack = new LoggingLanguage(settings ?? AllRules);

        Assert.Equal("LogTemplate", await pack.DetectAsync(document, token, model!, default));

        int position = token.SpanStart + 1;
        if (caretIn is not null)
        {
            int at = text.IndexOf(caretIn, index, StringComparison.Ordinal);
            Assert.True(at >= 0, $"{caretIn} is not in {literal}");
            position = at + 1;
        }
        else if (caretAfter is not null)
        {
            int at = text.IndexOf(caretAfter, index, StringComparison.Ordinal);
            Assert.True(at >= 0, $"{caretAfter} is not in {literal}");
            position = at + caretAfter.Length;
        }

        return (pack, new EmbeddedStringContext(
            pack, "LogTemplate", [], document, model!, token, position), text);
    }

    /// <summary>The text a diagnostic's range covers, for asserting where it landed.</summary>
    private static string At(string text, LspRange range)
    {
        var lines = Microsoft.CodeAnalysis.Text.SourceText.From(text).Lines;

        int start = lines[range.Start.Line].Start + range.Start.Character;
        int end = lines[range.End.Line].Start + range.End.Character;

        return text[start..end];
    }
}

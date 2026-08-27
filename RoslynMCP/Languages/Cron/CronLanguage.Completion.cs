using System.Globalization;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using Range = RoslynMCP.Lsp.Protocol.Range;

namespace RoslynMCP.Languages.Cron;

/// <summary>
/// Completion inside a schedule: the values this field accepts, and what each one means.
/// </summary>
/// <remarks>
/// <para>
/// The list is the documentation. Nobody remembers which position is the day of the month and which
/// is the day of the week, or that Quartz numbers Sunday differently from everyone else, and the way
/// that normally gets settled is by deploying and waiting a week. An offer that names the day beside
/// the number answers it where the question is asked.
/// </para>
/// <para>
/// What is offered is decided by the field the caret is in, so the same digit means January in one
/// position and Monday in another — which is the fact the whole pack exists to make visible.
/// </para>
/// </remarks>
internal sealed partial class CronLanguage : IEmbeddedCompletionProvider
{
    private static readonly CompletionList Empty = new(false, []);

    /// <summary>
    /// Whole expressions worth offering into an empty literal, which is where a schedule usually
    /// starts life.
    /// </summary>
    private static readonly (string Text, string Meaning)[] s_starters =
    [
        ("* * * * *", "every minute"),
        ("*/5 * * * *", "every 5 minutes"),
        ("0 * * * *", "every hour, on the hour"),
        ("0 3 * * *", "every day at 03:00"),
        ("0 3 * * 1", "every Monday at 03:00"),
        ("0 3 1 * *", "the 1st of every month, at 03:00"),
    ];

    public async Task<CompletionList> CompletionAsync(
        EmbeddedStringContext context, CompletionParams p, CancellationToken ct)
    {
        if (Resolve(context, ct) is not { Exact: true } at)
            return Empty;

        int offset = context.Position - at.Offset;
        if (offset < 0 || offset > at.Text.Length)
            return Empty;

        var text = await context.Document.GetTextAsync(ct);

        // Nothing has been written yet, or what was written is a macro. Either way the caret is
        // choosing a whole expression rather than a field of one.
        if (at.Parse.Fields.IsDefaultOrEmpty)
        {
            var whole = LspConverters.ToRange(
                text.Lines, at.InDocument(new TextSpan(0, at.Text.Length)));
            return Expressions(at, whole);
        }

        if (at.Parse.FieldAt(offset) is not { } field)
            return Empty;

        // The term under the caret is what gets replaced, so retyping one field leaves the rest of
        // the schedule alone.
        var replaced = at.Parse.TermAt(offset) is { Span.Length: > 0 } term
            ? at.InDocument(term.Span)
            : new TextSpan(context.Position, 0);

        return Values(field, at.Parse.Dialect, LspConverters.ToRange(text.Lines, replaced));
    }

    private static CompletionList Expressions(CronAt at, Range range)
    {
        var items = new List<CompletionItem>();
        int order = 0;

        foreach (var (expression, meaning) in s_starters)
        {
            items.Add(new CompletionItem(
                expression, LspCompletionItemKind.Snippet, meaning,
                order++.ToString("D2", CultureInfo.InvariantCulture), expression,
                new TextEdit(range, expression)));
        }

        foreach (var (macro, stands) in Cron.Macros(at.Parse.Dialect))
        {
            items.Add(new CompletionItem(
                macro, LspCompletionItemKind.Keyword, $"same as {stands}",
                order++.ToString("D2", CultureInfo.InvariantCulture), macro,
                new TextEdit(range, macro)));
        }

        return new CompletionList(false, [.. items]);
    }

    private static CompletionList Values(CronField field, CronDialect dialect, Range range)
    {
        var items = new List<CompletionItem>();
        int order = 0;

        Add("*", $"every one of the {Cron.Unit(field.Kind)}", LspCompletionItemKind.Keyword);

        var (min, max) = Cron.RangeOf(field.Kind, dialect);

        // A year runs to a hundred and thirty values and none of them is worth offering by name;
        // every other field is short enough that the whole list is the useful list.
        if (field.Kind != CronFieldKind.Year)
        {
            for (int value = min; value <= max; value++)
            {
                string label = value.ToString(CultureInfo.InvariantCulture);
                string? name = Cron.NameOf(field.Kind, value, dialect);

                // Sunday twice, under a plain crontab, because it genuinely is both 0 and 7 there
                // — and a reader who has seen only one spelling should meet the other here rather
                // than in a schedule they cannot explain.
                Add(label, name ?? $"{CronMarkdown.Field(field.Kind)} {label}",
                    LspCompletionItemKind.EnumMember);
            }
        }

        foreach (var (marker, meaning) in Markers(field.Kind))
            Add(marker, meaning, LspCompletionItemKind.Constant);

        return new CompletionList(false, [.. items]);

        void Add(string label, string detail, int kind) =>
            items.Add(new CompletionItem(
                label, kind, detail, order++.ToString("D3", CultureInfo.InvariantCulture), label,
                new TextEdit(range, label)));
    }

    /// <summary>The markers this field has a use for, with what each one does.</summary>
    private static IEnumerable<(string Marker, string Meaning)> Markers(CronFieldKind kind)
    {
        switch (kind)
        {
            case CronFieldKind.DayOfMonth:
                yield return ("?", "leave the day to the day-of-week field");
                yield return ("L", "the last day of the month");
                yield return ("LW", "the last weekday of the month");
                yield return ("15W", "the weekday nearest the 15th");
                break;

            case CronFieldKind.DayOfWeek:
                yield return ("?", "leave the day to the day-of-month field");
                yield return ("5L", "the last Friday of the month");
                yield return ("5#3", "the third Friday of the month");
                break;
        }
    }
}

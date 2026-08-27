using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.Cron;

/// <summary>
/// Hover on a schedule, or on one field of it: what it means and when it next comes round.
/// </summary>
/// <remarks>
/// The question a crontab expression raises is always the same one — <i>when does this actually
/// run?</i> — and until now the only way to answer it was to paste the string into a website. Three
/// real dates answer it in place, and the field under the caret is what says which position the
/// reader is looking at, which is the other half of every mistake made with one.
/// </remarks>
internal sealed partial class CronLanguage : IEmbeddedHoverProvider
{
    /// <summary>What separates the term from the schedule it belongs to.</summary>
    private const string Rule = "\n\n---\n\n";

    public async Task<Hover?> HoverAsync(EmbeddedStringContext context, CancellationToken ct)
    {
        if (Resolve(context, ct) is not { Exact: true } at)
            return null;

        int offset = context.Position - at.Offset;
        if (offset < 0 || offset > at.Text.Length)
            return null;

        var text = await context.Document.GetTextAsync(ct);
        var now = DateTime.Now;

        string schedule = CronMarkdown.Schedule(at.Parse, at.Text, now);

        // A caret on a term explains that term first, and then the schedule it belongs to. Both,
        // rather than one or the other: the fields of a crontab expression sit shoulder to shoulder
        // with no gap between them, so every caret inside one is a caret on some term, and a hover
        // that answered only "this is the day of the week" would never once say when the job runs.
        if (at.Parse.TermAt(offset) is { } term
            && at.Parse.FieldAt(offset) is { } field
            && term.Span.Length > 0)
        {
            return new Hover(
                new MarkupContent(
                    "markdown",
                    CronMarkdown.Term(term, field.Kind, at.Parse.Dialect)
                        + Rule + schedule),
                LspConverters.ToRange(text.Lines, at.InDocument(term.Span)));
        }

        return new Hover(
            new MarkupContent("markdown", schedule),
            LspConverters.ToRange(text.Lines, at.InDocument(new TextSpan(0, at.Text.Length))));
    }
}

using Microsoft.CodeAnalysis.Text;

namespace RoslynMCP.Languages.Cron;

/// <summary>
/// Colour for the fields of a schedule.
/// </summary>
/// <remarks>
/// <para>
/// The cheapest half of the pack and the one that pays every time the file is opened. A crontab
/// expression is read by counting positions, and the mistakes people make with one are miscounts:
/// <c>0 3 * * 1</c> runs weekly and <c>0 3 1 * *</c> runs monthly, they are one transposition apart,
/// and in a plain string they are the same shape. Six colours in a fixed order make them two
/// different shapes.
/// </para>
/// <para>
/// Only when the mapping is exact. A literal that escapes anything maps its offsets to the wrong
/// characters, and colour over the wrong two characters is worse than the plain string it was.
/// </para>
/// </remarks>
internal sealed partial class CronLanguage : IEmbeddedSemanticTokensProvider
{
    public Task<IReadOnlyList<EmbeddedToken>> SemanticTokensAsync(
        EmbeddedStringContext context, CancellationToken ct)
    {
        if (Resolve(context, ct) is not { Exact: true } at)
            return Task.FromResult<IReadOnlyList<EmbeddedToken>>([]);

        var tokens = new List<EmbeddedToken>();

        // A macro stands for a whole expression rather than a field, so it is coloured as the one
        // word it is. There is nothing inside it to separate.
        if (at.Parse.Macro is not null)
        {
            tokens.Add(new EmbeddedToken(
                at.InDocument(new TextSpan(0, at.Text.Length)), CronColours.Macro));
            return Task.FromResult<IReadOnlyList<EmbeddedToken>>(tokens);
        }

        foreach (var field in at.Parse.Fields)
        {
            ct.ThrowIfCancellationRequested();

            string colour = CronColours.For(field.Kind);
            int previous = -1;

            foreach (var term in field.Terms)
            {
                // The comma between two terms of one field. Painted as punctuation so that a list
                // reads as a list rather than as one long value.
                if (previous >= 0 && term.Span.Start > previous)
                {
                    tokens.Add(new EmbeddedToken(
                        at.InDocument(TextSpan.FromBounds(previous, term.Span.Start)),
                        CronColours.Separator));
                }

                if (term.Span.Length > 0)
                    tokens.Add(new EmbeddedToken(at.InDocument(term.Span), colour));

                previous = term.Span.End;
            }
        }

        return Task.FromResult<IReadOnlyList<EmbeddedToken>>(tokens);
    }
}

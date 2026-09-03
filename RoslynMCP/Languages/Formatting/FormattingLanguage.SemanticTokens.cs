using Microsoft.CodeAnalysis.Text;

namespace RoslynMCP.Languages.Formatting;

/// <summary>
/// Colour for the holes and for the components inside them.
/// </summary>
/// <remarks>
/// <para>
/// The whole point of the pack. A format specifier is a run of letters inside a string, and the
/// grammar paints it the same colour as the prose around it — so <c>dd-MM-yyyy</c> and
/// <c>dd-mm-yyyy</c> are visually identical, and only one of them prints a month. Giving the day,
/// the month and the year three different colours makes the pair different at a glance, before
/// anyone reads the letters.
/// </para>
/// <para>
/// Only when the mapping is exact. A literal that escapes anything maps its offsets to the wrong
/// characters, and colour over the wrong two characters is worse than the plain string it was.
/// </para>
/// </remarks>
internal sealed partial class FormattingLanguage : IEmbeddedSemanticTokensProvider
{
    public Task<IReadOnlyList<EmbeddedToken>> SemanticTokensAsync(
        EmbeddedStringContext context, CancellationToken ct)
    {
        if (Resolve(context, ct) is not { Exact: true } at)
            return Task.FromResult<IReadOnlyList<EmbeddedToken>>([]);

        var tokens = new List<EmbeddedToken>();

        foreach (var hole in Holes(at))
        {
            ct.ThrowIfCancellationRequested();

            Punctuate(tokens, at, hole);

            string specifier = at.Text[hole.Specifier.Start..hole.Specifier.End];
            var family = at.Family(hole.Index);

            foreach (var part in FormatString.Parts(specifier, family))
            {
                if (FormatColours.For(part.Kind) is not { } colour)
                    continue;

                tokens.Add(new EmbeddedToken(
                    at.InDocument(new TextSpan(hole.Specifier.Start + part.Span.Start, part.Span.Length)),
                    colour));
            }
        }

        return Task.FromResult<IReadOnlyList<EmbeddedToken>>(tokens);
    }

    /// <summary>
    /// The parts of a hole that are not the specifier: the braces, the index, the alignment.
    /// </summary>
    /// <remarks>
    /// Emits nothing for the implicit hole a lone specifier is — every one of these spans is empty
    /// there — so <c>$"{value:yyyyMMdd}"</c> gets the components coloured and the braces left to
    /// C#, which already colours them as the interpolation punctuation they are.
    /// </remarks>
    private static void Punctuate(List<EmbeddedToken> tokens, FormatAt at, FormatHole hole)
    {
        Add(TextSpan.FromBounds(hole.Span.Start, hole.NameSpan.Start), FormatColours.Punctuation);
        Add(hole.NameSpan, FormatColours.Value);

        if (hole.Alignment.Length > 0)
        {
            Add(TextSpan.FromBounds(hole.NameSpan.End, hole.Alignment.Start), FormatColours.Punctuation);
            Add(hole.Alignment, "number");
            Add(TextSpan.FromBounds(hole.Alignment.End, hole.Specifier.Start), FormatColours.Punctuation);
        }
        else
        {
            Add(TextSpan.FromBounds(hole.NameSpan.End, hole.Specifier.Start), FormatColours.Punctuation);
        }

        Add(TextSpan.FromBounds(hole.Specifier.End, hole.Span.End), FormatColours.Punctuation);

        void Add(TextSpan span, string colour)
        {
            if (span.Length > 0)
                tokens.Add(new EmbeddedToken(at.InDocument(span), colour));
        }
    }
}

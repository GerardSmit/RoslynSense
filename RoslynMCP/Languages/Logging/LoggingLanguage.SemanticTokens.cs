using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Logging.Core;

namespace RoslynMCP.Languages.Logging;

/// <summary>
/// Colour for the holes: the braces as punctuation, the name as the value it stands for.
/// </summary>
/// <remarks>
/// <para>
/// The colour carries one bit of information rather than decoration. A hole that reaches a value is
/// painted as a parameter; a hole that reaches nothing is left the colour of the string around it,
/// because it <i>is</i> string — it prints as the literal text <c>{Whatever}</c> and the reader
/// should see that before they see the squiggle.
/// </para>
/// <para>
/// C#'s own token names, so a theme that already colours parameters colours these. See
/// <see cref="IEmbeddedSemanticTokensProvider"/> for why a pack that owns a file type may add names
/// to the legend and this may not.
/// </para>
/// </remarks>
internal sealed partial class LoggingLanguage : IEmbeddedSemanticTokensProvider
{
    public Task<IReadOnlyList<EmbeddedToken>> SemanticTokensAsync(
        EmbeddedStringContext context, CancellationToken ct)
    {
        if (!Settings.Enabled || Resolve(context, ct) is not { Exact: true } at)
            return Task.FromResult<IReadOnlyList<EmbeddedToken>>([]);

        var tokens = new List<EmbeddedToken>(at.Template.Holes.Length * 3);

        foreach (var bound in HoleBinding.Bind(at.Template, at.Site))
        {
            var hole = bound.Hole;

            // The opening brace and whatever capture operator follows it, as one run.
            tokens.Add(new EmbeddedToken(
                at.InDocument(TextSpan.FromBounds(hole.Span.Start, hole.NameSpan.Start)), "operator"));

            // Left alone when nothing binds: the hole prints as literal text, and colouring it
            // like a value would be the editor agreeing with a mistake.
            if (bound.Value is not null || !at.Site.ValuesAreComplete)
                tokens.Add(new EmbeddedToken(at.InDocument(hole.NameSpan), "parameter"));

            tokens.Add(new EmbeddedToken(
                at.InDocument(new TextSpan(hole.Span.End - 1, 1)), "operator"));
        }

        return Task.FromResult<IReadOnlyList<EmbeddedToken>>(tokens);
    }
}

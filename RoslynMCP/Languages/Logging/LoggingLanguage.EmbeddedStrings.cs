using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Logging.Core;

namespace RoslynMCP.Languages.Logging;

/// <summary>
/// A message template inside a C# string literal, claimed by the pack rather than found by Roslyn.
/// </summary>
/// <remarks>
/// No <c>[StringSyntax]</c> could make this work. Serilog's <c>messageTemplate</c> parameter is not
/// annotated, nobody outside those repositories can annotate it, and the attribute could not say
/// the thing that matters anyway: what a hole means depends on the <i>arguments beside the
/// literal</i>, which is a property of the call and not of the parameter. See
/// <see cref="IConfiguredStringLanguage"/>.
/// </remarks>
internal sealed partial class LoggingLanguage : IConfiguredStringLanguage
{
    /// <summary>What a claimed token reports as its language, and what <c>// lang=logtemplate</c>
    /// above a literal names.</summary>
    private const string SyntaxIdentifier = "LogTemplate";

    public ImmutableArray<string> StringSyntaxIdentifiers { get; } =
        [SyntaxIdentifier, "MessageTemplate"];

    public Task<string?> DetectAsync(
        Document document, SyntaxToken token, SemanticModel semanticModel, CancellationToken ct) =>
        Task.FromResult(
            LogCallSite.CouldBeTemplate(token) && LogCallSite.Resolve(semanticModel, token, ct) is not null
                ? SyntaxIdentifier
                : null);

    /// <summary>
    /// The literal's text, its parsed template, and what binds it — everything every feature in
    /// the pack starts from.
    /// </summary>
    /// <param name="Offset">Where the template text begins in the document.</param>
    /// <param name="Exact">Whether an offset inside the template text is an offset in the document.
    /// False once the literal escapes anything, since one <c>\n</c> in the source is one character
    /// in the value and two in the file.</param>
    private readonly record struct TemplateAt(
        LogCallSite Site,
        MessageTemplate Template,
        SyntaxToken Token,
        int Offset,
        bool Exact)
    {
        /// <summary>A span inside the template, as a span in the document.</summary>
        /// <remarks>
        /// Collapses to the whole literal when the mapping is not exact. A squiggle over the string
        /// is a worse answer than one over the hole, and a squiggle over the wrong four characters
        /// is worse than both.
        /// </remarks>
        public TextSpan InDocument(TextSpan inTemplate) =>
            Exact ? new TextSpan(Offset + inTemplate.Start, inTemplate.Length) : Token.Span;
    }

    private static TemplateAt? Resolve(EmbeddedStringContext context, CancellationToken ct)
    {
        if (LogCallSite.Resolve(context.SemanticModel, context.Token, ct) is not { } site)
            return null;

        var token = context.Token;
        string raw = token.Text;

        // `@"…"` opens with two characters and doubles its quotes; everything else opens with one
        // and escapes with a backslash. Raw string literals never reach here — they are their own
        // token kind, which the syntax gate does not accept.
        bool verbatim = raw.StartsWith("@\"", StringComparison.Ordinal);
        int prefix = verbatim ? 2 : 1;
        bool exact = verbatim
            ? !raw.AsSpan(prefix).Contains("\"\"", StringComparison.Ordinal)
            : !raw.Contains('\\');

        return new TemplateAt(
            site, MessageTemplate.Parse(token.ValueText), token, token.SpanStart + prefix, exact);
    }

    /// <summary>The hole the caret is in, or the one it is about to open.</summary>
    private static BoundHole? HoleAt(TemplateAt at, int position)
    {
        if (!at.Exact)
            return null;

        foreach (var bound in HoleBinding.Bind(at.Template, at.Site))
        {
            var span = at.InDocument(bound.Hole.Span);

            // Inclusive of the end so a caret just past the closing brace still explains the hole
            // it is touching, the way hover over an identifier's last character does.
            if (position >= span.Start && position <= span.End)
                return bound;
        }

        return null;
    }
}

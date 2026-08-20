using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Formatting.Core;

namespace RoslynMCP.Languages.Formatting;

/// <summary>
/// A format string inside a C# literal, claimed from the call around it when nothing annotated it.
/// </summary>
/// <remarks>
/// Both routes matter, and which one answers depends on the target framework. A modern BCL
/// annotates <c>string.Format</c> and <c>DateTime.ToString</c> with <c>[StringSyntax]</c>, and
/// Roslyn's own detector finds those before this is consulted — the identifiers below are what
/// registers the pack for them. A .NET Framework solution has none of those annotations, and its
/// <c>string.Format</c> looks exactly the same to the person reading it, so the call itself has to
/// be the signal. See <see cref="IConfiguredStringLanguage"/>.
/// </remarks>
internal sealed partial class FormattingLanguage : IConfiguredStringLanguage
{
    /// <summary>The identifier a whole composite string is claimed under.</summary>
    private const string CompositeSyntax = "CompositeFormat";

    /// <summary>The identifier a lone specifier is claimed under.</summary>
    private const string SpecifierSyntax = "DateTimeFormat";

    /// <summary>
    /// The <c>StringSyntaxAttribute</c> constants this pack answers to.
    /// </summary>
    /// <remarks>
    /// Everything the BCL annotates a format string with, so an annotated call is claimed by
    /// Roslyn's detector and never reaches <see cref="DetectAsync"/>. <c>GuidFormat</c> and
    /// <c>EnumFormat</c> are absent: their specifiers are single letters naming whole shapes, with
    /// no components to colour and nothing a worked example would add.
    /// </remarks>
    public ImmutableArray<string> StringSyntaxIdentifiers { get; } =
    [
        CompositeSyntax,
        SpecifierSyntax,
        "NumericFormat",
        "DateOnlyFormat",
        "TimeOnlyFormat",
        "TimeSpanFormat",
    ];

    public Task<string?> DetectAsync(
        Document document, SyntaxToken token, SemanticModel semanticModel, CancellationToken ct) =>
        Task.FromResult(
            FormatSite.CouldBeFormat(token)
            && FormatSite.Resolve(semanticModel, token, ct) is { } site
                ? site.Kind == FormatTextKind.Composite ? CompositeSyntax : SpecifierSyntax
                : null);

    /// <summary>
    /// The literal's text, what binds it, and how to map an offset inside it back to the document —
    /// everything every feature in the pack starts from.
    /// </summary>
    /// <param name="Offset">Where the format text begins in the document.</param>
    /// <param name="Exact">Whether an offset inside the text is an offset in the document. False
    /// once the literal escapes anything, since one <c>\n</c> in the source is one character in the
    /// value and two in the file.</param>
    /// <param name="Declared">The family the annotation named, for the literals Roslyn claimed and
    /// whose value this pack never saw.</param>
    private readonly record struct FormatAt(
        FormatSite Site,
        string Text,
        SyntaxToken Token,
        int Offset,
        bool Exact,
        FormatFamily Declared)
    {
        /// <summary>A span inside the format text, as a span in the document.</summary>
        /// <remarks>
        /// Collapses to the whole literal when the mapping is not exact. A colour over the string
        /// is a worse answer than one over the component, and a colour over the wrong two
        /// characters is worse than both.
        /// </remarks>
        public TextSpan InDocument(TextSpan inside) =>
            Exact ? new TextSpan(Offset + inside.Start, inside.Length) : Token.Span;

        /// <summary>The value a hole renders, or null when the call did not say which.</summary>
        public FormatValue? Value(int index) =>
            index >= 0 && index < Site.Values.Length ? Site.Values[index] : null;

        /// <summary>Which grammar a hole's specifier is read with.</summary>
        public FormatFamily Family(int index) =>
            Value(index) is { } value ? Or(FormatFamilies.Of(value.Type)) : Declared;

        private FormatFamily Or(FormatFamily found) =>
            found == FormatFamily.Unknown ? Declared : found;
    }

    private static FormatAt? Resolve(EmbeddedStringContext context, CancellationToken ct)
    {
        var token = context.Token;

        // Roslyn's detector reaches literals this pack's own rule does not — a `[StringSyntax]` on
        // a parameter of the user's own method, say — and the annotation is enough to read the
        // string even though nothing here can name the value it formats.
        var site = FormatSite.Resolve(context.SemanticModel, token, ct)
            ?? new FormatSite(
                context.Identifier == CompositeSyntax
                    ? FormatTextKind.Composite
                    : FormatTextKind.Specifier,
                [], ValuesAreComplete: false, context.Identifier);

        string raw = token.Text;

        // The text of an interpolation's format clause has no quotes of its own; every other
        // candidate is a literal, and `@"…"` opens with two characters where the rest open with
        // one. Raw string literals never reach here — they are their own token kind, which the
        // syntax gate does not accept.
        bool clause = token.IsKind(SyntaxKind.InterpolatedStringTextToken);
        bool verbatim = !clause && raw.StartsWith("@\"", StringComparison.Ordinal);
        int prefix = clause ? 0 : verbatim ? 2 : 1;

        bool exact = !raw.Contains('\\')
            && !raw.AsSpan(prefix).Contains("\"\"", StringComparison.Ordinal);

        return new FormatAt(
            site, token.ValueText, token, token.SpanStart + prefix, exact,
            Declared(context.Identifier));
    }

    /// <summary>The family an annotation named, when one did.</summary>
    private static FormatFamily Declared(string identifier) => identifier switch
    {
        "NumericFormat" => FormatFamily.Number,
        SpecifierSyntax or "DateOnlyFormat" or "TimeOnlyFormat" => FormatFamily.Date,
        _ => FormatFamily.Unknown,
    };

    /// <summary>
    /// The holes of the text, or the single implicit one a lone specifier is.
    /// </summary>
    /// <remarks>
    /// <c>$"{value:yyyyMMdd}"</c> and <c>string.Format("{0:yyyyMMdd}", value)</c> are the same
    /// question written twice, and the only difference is that the first has already been split
    /// into a value and a specifier by the compiler. Giving the second shape a hole spanning the
    /// whole text lets everything downstream stop caring which one it is looking at.
    /// </remarks>
    private static ImmutableArray<FormatHole> Holes(FormatAt at) =>
        at.Site.Kind == FormatTextKind.Composite
            ? FormatString.Holes(at.Text)
            : [new FormatHole(
                new TextSpan(0, at.Text.Length), 0, string.Empty,
                new TextSpan(0, 0), new TextSpan(0, 0), new TextSpan(0, at.Text.Length))];
}

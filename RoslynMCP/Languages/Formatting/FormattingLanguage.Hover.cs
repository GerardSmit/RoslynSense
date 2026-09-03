using System.Text;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Formatting.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.Formatting;

/// <summary>
/// Hover on a hole or on one of its components: what it prints, worked out rather than described.
/// </summary>
/// <remarks>
/// The question a format string raises is always the same one — <i>what does this actually
/// produce?</i> — and until now the only way to answer it was to run the page. An example rendered
/// from a fixed date answers it in place, and the component under the caret is what says which half
/// of a two-word specifier the reader is looking at.
/// </remarks>
internal sealed partial class FormattingLanguage : IEmbeddedHoverProvider
{
    public async Task<Hover?> HoverAsync(EmbeddedStringContext context, CancellationToken ct)
    {
        if (Resolve(context, ct) is not { Exact: true } at)
            return null;

        int offset = context.Position - at.Offset;
        if (offset < 0 || offset > at.Text.Length)
            return null;

        if (FormatString.HoleAt(Holes(at), offset) is not { } hole)
            return null;

        var family = at.Family(hole.Index);
        string specifier = at.Text[hole.Specifier.Start..hole.Specifier.End];

        var parts = FormatString.Parts(specifier, family);
        var part = FormatString.PartAt(parts, offset - hole.Specifier.Start);

        var text = await context.Document.GetTextAsync(ct);

        // A caret on the literal text between two components has no component of its own to
        // explain, so it falls through to the hole — which is also what a caret on the braces or
        // the index wants.
        if (part is { Kind: not (FormatPartKind.Literal or FormatPartKind.Escape) } component)
        {
            return new Hover(
                new MarkupContent("markdown", FormatMarkdown.Component(component, specifier, family)),
                LspConverters.ToRange(text.Lines, at.InDocument(
                    new TextSpan(hole.Specifier.Start + component.Span.Start, component.Span.Length))));
        }

        return new Hover(
            new MarkupContent("markdown", FormatMarkdown.Hole(at.Text, hole, family, Value(at, hole))),
            LspConverters.ToRange(text.Lines, at.InDocument(hole.Span)));
    }

    /// <summary>
    /// The value the hole renders, named and typed — which is the arithmetic nobody does.
    /// </summary>
    /// <remarks>
    /// A composite hole binds <b>by position</b>: <c>{1}</c> is the second value after the format
    /// string, and reading the call tells you which one only after you have counted. That the
    /// counting is silent is why <c>string.Format("{0:dd-MM-yyyy}", name, date)</c> compiles, runs,
    /// and prints a name.
    /// </remarks>
    private static string? Value(FormatAt at, FormatHole hole)
    {
        if (at.Value(hole.Index) is not { } value)
        {
            return at.Site.Kind == FormatTextKind.Composite && at.Site.ValuesAreComplete
                ? $"The call passes no {Ordinal(hole.Index)} value, so this throws at run time."
                : null;
        }

        var builder = new StringBuilder("```csharp\n");

        if (value.Type is { } type)
            builder.Append(type.ToDisplayString(Services.Symbols.MemberSignature.TypeName)).Append(' ');

        builder.Append(value.Name).Append("\n```");

        if (at.Site.Kind == FormatTextKind.Composite)
        {
            builder.Append("\n\nThe ").Append(Ordinal(hole.Index)).Append(" value passed to `")
                .Append(at.Site.Subject).Append("` — matched by position, not by name.");
        }

        return builder.ToString();
    }

    private static string Ordinal(int index) => (index + 1) switch
    {
        1 => "1st",
        2 => "2nd",
        3 => "3rd",
        var n when n % 10 == 1 && n % 100 != 11 => $"{n}st",
        var n when n % 10 == 2 && n % 100 != 12 => $"{n}nd",
        var n when n % 10 == 3 && n % 100 != 13 => $"{n}rd",
        var n => $"{n}th",
    };
}

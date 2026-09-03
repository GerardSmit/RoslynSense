using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMCP.Languages.Logging.Core;

/// <summary>How a hole names the value it renders.</summary>
internal enum HoleKind
{
    /// <summary><c>{OrderId}</c> — a property name.</summary>
    Named,

    /// <summary><c>{0}</c> — an index into the values, the old composite-format spelling.</summary>
    Positional,
}

/// <summary>
/// The capture hint a hole may carry, from the message-templates specification that Serilog and
/// NLog both implement.
/// </summary>
/// <remarks>
/// Microsoft.Extensions.Logging has no notion of either: its formatter takes the name verbatim, so
/// <c>{@Order}</c> there produces a property literally called <c>@Order</c>. The prefix is still
/// parsed for MEL, because a solution logging through MEL into a Serilog sink writes it deliberately
/// and a hole reported as unbound over it would be wrong.
/// </remarks>
internal enum CaptureHint
{
    None,

    /// <summary><c>{@Order}</c> — capture the object's structure rather than its ToString.</summary>
    Destructure,

    /// <summary><c>{$Order}</c> — force the string representation.</summary>
    Stringify,
}

/// <summary>
/// One <c>{…}</c> in a template. Spans are relative to the template text, not to the document —
/// the literal's own offset is added at the seam that knows it.
/// </summary>
/// <param name="Span">The whole hole including its braces.</param>
/// <param name="NameSpan">Just the name, which is what completion replaces and hover explains.</param>
/// <param name="Ordinal">Position among the holes, counted from 0. This is what a positionally
/// bound framework — every one of them except the source generator — actually uses.</param>
/// <param name="Index">The number a <see cref="HoleKind.Positional"/> hole wrote, else -1.</param>
internal readonly record struct TemplateHole(
    TextSpan Span,
    TextSpan NameSpan,
    string Name,
    HoleKind Kind,
    int Ordinal,
    int Index,
    CaptureHint Hint,
    string? Alignment,
    string? Format);

/// <summary>Something wrong with the template text itself, before any argument is considered.</summary>
internal readonly record struct TemplateProblem(TextSpan Span, string Message);

/// <summary>
/// A logging message template, parsed.
/// </summary>
/// <remarks>
/// One parser for all four dialects, because they agree about everything that matters here: holes
/// in braces, doubled braces for a literal one, an optional alignment after a comma and an optional
/// format after a colon. Where they differ is what a *name* may contain — Serilog requires
/// <c>[0-9A-Za-z_]</c> and treats anything else as ordinary text, while MEL takes whatever is in
/// front of the comma — and that difference is reported rather than parsed two ways: a name Serilog
/// would not accept is a hole that silently prints as literal text, which is exactly the kind of
/// thing a squiggle should be pointing at.
/// </remarks>
internal sealed record MessageTemplate(
    ImmutableArray<TemplateHole> Holes,
    ImmutableArray<TemplateProblem> Problems)
{
    public static MessageTemplate Empty { get; } = new([], []);

    /// <summary>
    /// True when every hole is a number. Composite-format templates bind by that number rather
    /// than by order of appearance, so <c>"{1} {0}"</c> is not two values reversed.
    /// </summary>
    public bool IsPositional => Holes.Length > 0 && Holes.All(hole => hole.Kind == HoleKind.Positional);

    /// <summary>
    /// How many values the template consumes: the hole count, or one past the highest index when
    /// the holes are numbered.
    /// </summary>
    public int ValueCount =>
        IsPositional ? Holes.Max(hole => hole.Index) + 1 : Holes.Length;

    public static MessageTemplate Parse(string text)
    {
        if (text.IndexOf('{') < 0)
            return Empty;

        var holes = ImmutableArray.CreateBuilder<TemplateHole>();
        var problems = ImmutableArray.CreateBuilder<TemplateProblem>();

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c == '}')
            {
                // A lone closing brace is a format error everywhere, and in MEL it is a thrown
                // FormatException rather than a rendering oddity.
                if (i + 1 < text.Length && text[i + 1] == '}')
                    i++;
                else
                    problems.Add(new TemplateProblem(new TextSpan(i, 1), "Unmatched '}'. Write '}}' for a literal one."));

                continue;
            }

            if (c != '{')
                continue;

            if (i + 1 < text.Length && text[i + 1] == '{')
            {
                i++;
                continue;
            }

            int end = text.IndexOf('}', i + 1);
            if (end < 0)
            {
                problems.Add(new TemplateProblem(
                    new TextSpan(i, text.Length - i), "Unclosed '{'. Write '{{' for a literal one."));
                break;
            }

            if (TryHole(text, i, end, holes.Count, out var hole, out string? problem))
                holes.Add(hole);
            else if (problem is not null)
                problems.Add(new TemplateProblem(new TextSpan(i, end - i + 1), problem));

            i = end;
        }

        return new MessageTemplate(holes.ToImmutable(), problems.ToImmutable());
    }

    private static bool TryHole(
        string text, int open, int close, int ordinal, out TemplateHole hole, out string? problem)
    {
        hole = default;
        problem = null;

        int at = open + 1;
        var hint = at < close
            ? text[at] switch
            {
                '@' => CaptureHint.Destructure,
                '$' => CaptureHint.Stringify,
                _ => CaptureHint.None,
            }
            : CaptureHint.None;

        if (hint != CaptureHint.None)
            at++;

        // The name runs to the alignment, the format or the closing brace, whichever comes first.
        int nameEnd = at;
        while (nameEnd < close && text[nameEnd] != ',' && text[nameEnd] != ':')
            nameEnd++;

        string name = text[at..nameEnd];
        if (name.Length == 0)
        {
            problem = "This hole names nothing.";
            return false;
        }

        string? alignment = null;
        string? format = null;

        int rest = nameEnd;
        if (rest < close && text[rest] == ',')
        {
            int alignmentEnd = rest + 1;
            while (alignmentEnd < close && text[alignmentEnd] != ':')
                alignmentEnd++;

            alignment = text[(rest + 1)..alignmentEnd];
            rest = alignmentEnd;
        }

        if (rest < close && text[rest] == ':')
            format = text[(rest + 1)..close];

        if (int.TryParse(name, out int index) && index >= 0)
        {
            hole = new TemplateHole(
                new TextSpan(open, close - open + 1), new TextSpan(at, name.Length), name,
                HoleKind.Positional, ordinal, index, hint, alignment, format);
            return true;
        }

        if (!IsName(name))
        {
            // Not a diagnostic in itself — plenty of templates contain `{` in prose — but it is
            // not a hole either, and treating it as one would report every value as unused.
            return false;
        }

        hole = new TemplateHole(
            new TextSpan(open, close - open + 1), new TextSpan(at, name.Length), name,
            HoleKind.Named, ordinal, -1, hint, alignment, format);
        return true;
    }

    /// <summary>
    /// The message-templates rule: letters, digits and underscore. Deliberately not C#'s identifier
    /// rules — a property name is a name in the log event, not in the language, and Serilog's
    /// parser is the one that decides whether the hole renders at all.
    /// </summary>
    private static bool IsName(string name)
    {
        foreach (char c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
                return false;
        }

        return true;
    }
}

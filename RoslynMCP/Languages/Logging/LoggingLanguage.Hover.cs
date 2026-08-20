using System.Text;
using RoslynMCP.Languages.Logging.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.Logging;

/// <summary>
/// Hover on a hole: which value it prints.
/// </summary>
/// <remarks>
/// The most useful thing in the pack, and the least obvious that it is needed. A hole at a call
/// site is bound <b>by position</b>, so <c>{User}</c> prints whatever the n-th argument happens to
/// be and the name is a label the sink stores. Reading the call tells you nothing until you have
/// counted the arguments and the holes and matched them up — which is exactly the arithmetic nobody
/// does, and exactly why the swapped pair survives review.
/// </remarks>
internal sealed partial class LoggingLanguage : IEmbeddedHoverProvider
{
    public async Task<Hover?> HoverAsync(EmbeddedStringContext context, CancellationToken ct)
    {
        if (!Settings.Enabled
            || Resolve(context, ct) is not { } at
            || HoleAt(at, context.Position) is not { } bound)
        {
            return null;
        }

        var text = await context.Document.GetTextAsync(ct);

        return new Hover(
            new MarkupContent("markdown", Describe(at, bound)),
            LspConverters.ToRange(text.Lines, at.InDocument(bound.Hole.Span)));
    }

    private static string Describe(TemplateAt at, BoundHole bound)
    {
        var builder = new StringBuilder("**").Append(bound.Hole.Name).Append("**");

        if (bound.Value is { } value)
        {
            builder.Append("\n\n```csharp\n").Append(value.Type).Append(' ').Append(value.Name)
                .Append("\n```\n\n").Append(Source(at, bound));
        }
        else if (at.Site.Binding == TemplateBinding.ByName)
        {
            builder.Append("\n\nNo parameter of `").Append(at.Site.Subject)
                .Append("` has this name, so it prints as literal text.");
        }
        else if (at.Site.ValuesAreComplete)
        {
            builder.Append("\n\nThe call passes no ").Append(Ordinal(bound.Position))
                .Append(" value, so this prints as literal text.");
        }

        switch (bound.Hole.Hint)
        {
            case CaptureHint.Destructure:
                builder.Append("\n\n`@` — captured as a structure rather than by `ToString()`.");
                break;
            case CaptureHint.Stringify:
                builder.Append("\n\n`$` — captured as a string.");
                break;
        }

        if (bound.Hole.Alignment is { Length: > 0 } alignment)
            builder.Append("\n\nPadded to ").Append(alignment).Append(" characters.");

        if (bound.Hole.Format is { Length: > 0 } format)
            builder.Append("\n\nFormatted with `").Append(format).Append("`.");

        return builder.ToString();
    }

    /// <summary>Where the value comes from, which is the whole difference between the two bindings.</summary>
    private static string Source(TemplateAt at, BoundHole bound) =>
        at.Site.Binding == TemplateBinding.ByName
            ? $"Matched by name to a parameter of `{at.Site.Subject}`."
            : $"The {Ordinal(bound.Position)} value passed to `{at.Site.Subject}` — matched by "
              + "position, not by name.";

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

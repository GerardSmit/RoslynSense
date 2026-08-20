using System.Text;

namespace RoslynMCP.Languages.Formatting;

/// <summary>
/// What a format string says, in words a reader can check against the output.
/// </summary>
/// <remarks>
/// Shared by both hosts on purpose. A <c>DataFormatString</c> on a grid column and a
/// <c>$"{value:…}"</c> in the code behind it are the same specifier read by the same runtime, and
/// a reader who learns what <c>MM</c> means from one hover has learned it for the other only if the
/// two hovers say the same thing.
/// <para>
/// Every claim here is rendered rather than asserted: the example comes from
/// <see cref="FormatString.Example"/> calling the runtime's own formatter, so a description and an
/// example cannot drift apart, and a specifier the runtime rejects says so instead of inventing
/// output for it.
/// </para>
/// </remarks>
internal static class FormatMarkdown
{
    /// <summary>The whole hole: what it renders, and what its specifier does to it.</summary>
    /// <param name="value">Markdown naming the value the hole prints, or null when nothing did.</param>
    public static string Hole(string text, FormatHole hole, FormatFamily family, string? value)
    {
        var builder = new StringBuilder("**")
            .Append(text[hole.Span.Start..hole.Span.End])
            .Append("**");

        if (value is { Length: > 0 })
            builder.Append("\n\n").Append(value);

        string specifier = text[hole.Specifier.Start..hole.Specifier.End];

        if (specifier.Length == 0)
        {
            return builder
                .Append("\n\nNo format specifier, so the value prints with its own `ToString()`.")
                .ToString();
        }

        Specifier(builder, specifier, family);
        return builder.ToString();
    }

    /// <summary>One component of a specifier, and the specifier it sits in.</summary>
    public static string Component(FormatPart part, string specifier, FormatFamily family)
    {
        var builder = new StringBuilder("**")
            .Append(part.Text)
            .Append("** — ")
            .Append(FormatString.Describe(part, family));

        // Not for a standard specifier, which is the whole text: the line below already renders it,
        // and repeating it immediately above reads as two different facts.
        if (part.Kind is not (FormatPartKind.Standard or FormatPartKind.Literal or FormatPartKind.Escape)
            && FormatString.Example(part.Text, family) is { } alone)
        {
            builder.Append("\n\nPrints `").Append(alone).Append("`.");
        }

        Specifier(builder, specifier, family);
        return builder.ToString();
    }

    /// <summary>
    /// The specifier as a whole: what it prints, and what each of its components contributes.
    /// </summary>
    /// <remarks>
    /// The table is the part worth having. Nobody misreads <c>dd</c> on its own; what goes wrong is
    /// <c>dd-mm-yyyy</c>, where the minute sits where the month should and every character is a
    /// character that belongs in a date. Listing the components beside their meanings is what makes
    /// that visible without running the page.
    /// </remarks>
    private static void Specifier(StringBuilder builder, string specifier, FormatFamily family)
    {
        builder.Append("\n\n`").Append(specifier).Append('`');

        if (FormatString.Example(specifier, family) is { } example)
            builder.Append(" → `").Append(example).Append('`');
        else
            builder.Append(" is not a format the runtime accepts.");

        var parts = FormatString.Parts(specifier, family)
            .Where(part => part.Kind is not (FormatPartKind.Literal or FormatPartKind.Escape))
            .ToArray();

        // One component means the line above already said everything the table would.
        if (parts.Length < 2)
            return;

        builder.Append("\n\n| | |\n| --- | --- |");

        foreach (var part in parts)
        {
            builder.Append("\n| `").Append(part.Text).Append("` | ")
                .Append(FormatString.Describe(part, family)).Append(" |");
        }
    }
}

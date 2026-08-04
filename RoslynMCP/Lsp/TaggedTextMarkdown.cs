using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;

namespace RoslynMCP.Lsp;

/// <summary>
/// Turns Roslyn's tagged description parts into markdown.
/// </summary>
/// <remarks>
/// <para>
/// Roslyn hands back a stream of <see cref="TaggedText"/> — every token labelled as a keyword,
/// a type name, a parameter, punctuation, a line break. Flattening it with
/// <c>description.Text</c> throws all of that away and produces one grey paragraph with the
/// signature run into the prose, which is what the completion popup was showing while claiming
/// to be markdown.
/// </para>
/// <para>
/// The signature goes in a fenced C# block so the client syntax-highlights it, and the
/// documentation follows as prose with parameter and type names kept as code spans. Markdown
/// specials in the prose are escaped: an XML doc comment mentioning <c>*</c> or <c>_</c> should
/// read as itself rather than turning the rest of the tooltip italic.
/// </para>
/// </remarks>
internal static class TaggedTextMarkdown
{
    public static string ToMarkdown(ImmutableArray<TaggedText> parts)
    {
        if (parts.IsDefaultOrEmpty)
            return "";

        // Roslyn puts the declaration first and breaks the line before the documentation.
        int split = parts.IndexOf(parts.FirstOrDefault(p => p.Tag == TextTags.LineBreak));
        if (split < 0)
            split = parts.Length;

        string signature = Concat(parts.Take(split)).Trim();
        string prose = Prose(parts.Skip(split + 1));

        var markdown = new StringBuilder();
        if (signature.Length > 0)
            markdown.Append("```csharp\n").Append(signature).Append("\n```");

        if (prose.Length > 0)
        {
            if (markdown.Length > 0)
                markdown.Append("\n\n");
            markdown.Append(prose);
        }

        return markdown.ToString();
    }

    private static string Concat(IEnumerable<TaggedText> parts)
    {
        var sb = new StringBuilder();
        foreach (var part in parts)
            sb.Append(part.Tag == TextTags.LineBreak ? "\n" : part.Text);
        return sb.ToString();
    }

    private static string Prose(IEnumerable<TaggedText> parts)
    {
        var sb = new StringBuilder();
        bool pendingBreak = false;

        foreach (var part in parts)
        {
            if (part.Tag == TextTags.LineBreak)
            {
                pendingBreak = true;
                continue;
            }

            if (pendingBreak)
            {
                // A blank line, so each XML doc section — summary, a parameter, returns —
                // becomes its own paragraph instead of one run-on sentence.
                if (sb.Length > 0)
                    sb.Append("\n\n");
                pendingBreak = false;
            }

            sb.Append(IsSymbol(part.Tag) && part.Text.Trim().Length > 0
                ? $"`{part.Text}`"
                : Escape(part.Text));
        }

        return sb.ToString().Trim();
    }

    /// <summary>Tags that name something in the code, and read better as a code span.</summary>
    private static bool IsSymbol(string tag) =>
        tag is TextTags.Parameter or TextTags.TypeParameter or TextTags.Keyword
            or TextTags.Class or TextTags.Struct or TextTags.Interface or TextTags.Enum
            or TextTags.Delegate or TextTags.Record or TextTags.Method or TextTags.Property
            or TextTags.Field or TextTags.Event or TextTags.Local or TextTags.Constant;

    /// <summary>
    /// Neutralises the markdown that a doc comment did not intend. Only the characters that
    /// start inline formatting — escaping more would litter the tooltip with backslashes.
    /// </summary>
    private static string Escape(string text)
    {
        if (!text.Any(c => c is '*' or '_' or '`' or '[' or ']' or '<' or '>'))
            return text;

        var sb = new StringBuilder(text.Length + 8);
        foreach (char c in text)
        {
            if (c is '*' or '_' or '`' or '[' or ']' or '<' or '>')
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}

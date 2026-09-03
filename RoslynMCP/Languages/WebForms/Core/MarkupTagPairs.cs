using Microsoft.CodeAnalysis.Text;

namespace RoslynMCP.Languages.WebForms.Core;

/// <summary>
/// An open tag and the close tag that ends it, both spans covering the name only.
/// </summary>
internal readonly record struct MarkupTagPair(TextSpan Open, TextSpan Close);

/// <summary>
/// Tag pairing for the markup the parser deliberately leaves as text.
/// </summary>
/// <remarks>
/// <para>
/// A lowercase HTML tag with no <c>runat="server"</c> — <c>&lt;div&gt;</c>, <c>&lt;span&gt;</c>,
/// <c>&lt;td&gt;</c> — never becomes an <c>ElementNode</c>: it is literal output, so the parser
/// emits it as text and only remembers enough about it to report an unbalanced close tag. That is
/// the right shape for code generation and the wrong shape for the editor features that just want
/// to know which two tags are the same tag, so those get this scanner instead.
/// </para>
/// <para>
/// It reads the raw document rather than the tree, which is why it is careful about the places a
/// <c>&lt;</c> means nothing: server blocks, comments, and the body of a
/// <c>&lt;script&gt;</c> or <c>&lt;style&gt;</c>, where <c>a &lt; b</c> is arithmetic and not a
/// tag. Matching is by name and depth, and a name that never balances yields nothing — an
/// unclosed <c>&lt;li&gt;</c> is legal HTML, and guessing where its close tag would have gone is
/// the blind edit linked editing must not make.
/// </para>
/// </remarks>
internal static class MarkupTagPairs
{
    /// <summary>HTML elements that hold no content, so no close tag is ever theirs.</summary>
    private static readonly HashSet<string> s_voidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "command", "embed", "hr", "img", "input",
        "keygen", "link", "meta", "param", "source", "track", "wbr",
    };

    /// <summary>One tag as it appears in the text, named by the span of its name.</summary>
    private readonly record struct Tag(TextSpan Name, string Text, bool IsClosing, bool IsSelfClosing);

    /// <summary>
    /// The pair the caret is on, or <see langword="null"/> when the caret is not on a tag name or
    /// the tag it is on has no partner.
    /// </summary>
    public static MarkupTagPair? At(string text, int offset)
    {
        var tags = Scan(text);

        int index = tags.FindIndex(t => t.Name.Start <= offset && offset <= t.Name.End);
        if (index < 0)
            return null;

        var tag = tags[index];
        if (tag.IsSelfClosing || s_voidElements.Contains(tag.Text))
            return null;

        return tag.IsClosing ? MatchBackward(tags, index) : MatchForward(tags, index);
    }

    private static MarkupTagPair? MatchForward(List<Tag> tags, int index)
    {
        var open = tags[index];

        int depth = 1;
        for (int i = index + 1; i < tags.Count; i++)
        {
            var tag = tags[i];
            if (tag.IsSelfClosing || !SameName(tag, open))
                continue;

            depth += tag.IsClosing ? -1 : 1;
            if (depth == 0)
                return new MarkupTagPair(open.Name, tag.Name);
        }

        return null;
    }

    private static MarkupTagPair? MatchBackward(List<Tag> tags, int index)
    {
        var close = tags[index];

        int depth = 1;
        for (int i = index - 1; i >= 0; i--)
        {
            var tag = tags[i];
            if (tag.IsSelfClosing || !SameName(tag, close))
                continue;

            depth += tag.IsClosing ? 1 : -1;
            if (depth == 0)
                return new MarkupTagPair(tag.Name, close.Name);
        }

        return null;
    }

    private static bool SameName(Tag a, Tag b) =>
        string.Equals(a.Text, b.Text, StringComparison.OrdinalIgnoreCase);

    /// <summary>Every tag in the document, in source order.</summary>
    private static List<Tag> Scan(string text)
    {
        var tags = new List<Tag>();

        for (int i = 0; i < text.Length;)
        {
            if (text[i] != '<')
            {
                i++;
                continue;
            }

            // `<%= %>`, `<%# %>`, `<%-- --%>` and `<%@ %>` all end the same way, and none of them
            // contains markup this scanner has any business reading.
            if (Next(text, i) == '%')
            {
                i = Skip(text, i + 2, "%>");
                continue;
            }

            if (Next(text, i) == '!')
            {
                i = text.AsSpan(i).StartsWith("<!--", StringComparison.Ordinal)
                    ? Skip(text, i + 4, "-->")
                    : Skip(text, i + 2, ">");
                continue;
            }

            bool isClosing = Next(text, i) == '/';
            int nameStart = i + (isClosing ? 2 : 1);
            int nameEnd = nameStart;
            while (nameEnd < text.Length && IsNameChar(text[nameEnd], nameEnd == nameStart))
                nameEnd++;

            if (nameEnd == nameStart)
            {
                i++;
                continue;
            }

            string name = text[nameStart..nameEnd];
            var (end, selfClosing) = EndOfTag(text, nameEnd);

            tags.Add(new Tag(TextSpan.FromBounds(nameStart, nameEnd), name, isClosing, selfClosing));
            i = end;

            // Everything up to `</script>` is JavaScript, where `<` is an operator. The same goes
            // for a stylesheet body, and for both the close tag itself is found by name rather
            // than by parsing what is in between.
            if (!isClosing && !selfClosing
                && (name.Equals("script", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("style", StringComparison.OrdinalIgnoreCase)))
            {
                i = SkipToCloseTag(text, i, name);
            }
        }

        return tags;
    }

    /// <summary>
    /// Where the tag's <c>&gt;</c> leaves off, and whether that <c>&gt;</c> closed the element.
    /// </summary>
    /// <remarks>
    /// Quoted attribute values are stepped over so a <c>&gt;</c> inside one does not end the tag
    /// early, and so is a tag nested in the attribute list — <c>&lt;div &lt;asp:Literal
    /// runat="server" /&gt;&gt;</c> is markup the parser accepts, and its inner <c>/&gt;</c>
    /// belongs to the literal, not to the div.
    /// </remarks>
    private static (int End, bool SelfClosing) EndOfTag(string text, int start)
    {
        int nested = 0;
        char previous = '\0';

        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];

            if (c is '"' or '\'')
            {
                int close = text.IndexOf(c, i + 1);
                i = close < 0 ? text.Length : close;
                previous = c;
                continue;
            }

            if (c == '<')
            {
                if (Next(text, i) == '%')
                {
                    i = Skip(text, i + 2, "%>") - 1;
                    previous = '>';
                    continue;
                }

                nested++;
                continue;
            }

            if (c == '>')
            {
                if (nested > 0)
                {
                    nested--;
                    previous = c;
                    continue;
                }

                // A '/' that ended a nested tag is not this tag's own.
                return (i + 1, previous == '/');
            }

            previous = c;
        }

        return (text.Length, false);
    }

    /// <summary>The offset just past <c>&lt;/name&gt;</c>, or the end of the file without one.</summary>
    private static int SkipToCloseTag(string text, int start, string name)
    {
        for (int i = text.IndexOf('<', start); i >= 0; i = text.IndexOf('<', i + 1))
        {
            if (Next(text, i) != '/')
                continue;

            var rest = text.AsSpan(i + 2);
            if (rest.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                && (rest.Length == name.Length || !IsNameChar(rest[name.Length], first: false)))
            {
                return i;
            }
        }

        return text.Length;
    }

    private static char Next(string text, int i) => i + 1 < text.Length ? text[i + 1] : '\0';

    private static int Skip(string text, int from, string terminator)
    {
        int end = text.IndexOf(terminator, from, StringComparison.Ordinal);
        return end < 0 ? text.Length : end + terminator.Length;
    }

    private static bool IsNameChar(char c, bool first) =>
        char.IsLetter(c) || c == '_' || (!first && (char.IsDigit(c) || c is '.' or ':' or '-'));
}

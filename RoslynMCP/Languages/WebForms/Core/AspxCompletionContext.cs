using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.WebForms.Core;

internal enum AspxContextKind
{
    /// <summary>Plain markup text — nothing to complete.</summary>
    None,

    /// <summary>The tag name of an open tag: <c>&lt;asp:But|</c>.</summary>
    TagName,

    /// <summary>An attribute name inside an open tag: <c>&lt;asp:Button Te|</c>.</summary>
    AttributeName,

    /// <summary>An attribute value: <c>&lt;asp:Button Text="|"</c>.</summary>
    AttributeValue,

    /// <summary>Inside <c>&lt;% %&gt;</c>, <c>&lt;%= %&gt;</c> or <c>&lt;%# %&gt;</c>.</summary>
    Code,

    /// <summary>Inside a <c>&lt;%@ %&gt;</c> directive.</summary>
    Directive,
}

/// <summary>Where the caret is, as far as completion is concerned.</summary>
/// <param name="Kind">The kind of position.</param>
/// <param name="ReplaceSpan">What a committed item replaces.</param>
/// <param name="TagPrefix">The open tag's prefix, if it has one.</param>
/// <param name="TagName">The open tag's local name.</param>
/// <param name="AttributeName">The attribute being named or valued.</param>
/// <param name="TagStart">Offset of the tag's <c>&lt;</c>.</param>
internal sealed record AspxCompletionContext(
    AspxContextKind Kind,
    TextSpan ReplaceSpan,
    string? TagPrefix = null,
    string? TagName = null,
    string? AttributeName = null,
    int TagStart = -1)
{
    public static readonly AspxCompletionContext None =
        new(AspxContextKind.None, default);

    /// <summary>The tag as written, prefix included.</summary>
    public string? QualifiedTagName =>
        TagName is null ? null : TagPrefix is null ? TagName : TagPrefix + ":" + TagName;
}

/// <summary>
/// Classifies a caret position in ASPX markup by scanning the raw text rather than the parse
/// tree. Half-typed markup — <c>&lt;asp:But</c> with no closing bracket — is exactly the state
/// completion runs in, and it is exactly the state the parser cannot represent.
/// </summary>
internal static class AspxCompletionContextScanner
{
    public static AspxCompletionContext Classify(string text, int offset)
    {
        if (offset < 0 || offset > text.Length)
            return AspxCompletionContext.None;

        int open = text.LastIndexOf('<', Math.Max(0, offset - 1));
        if (open < 0)
            return AspxCompletionContext.None;

        if (open + 1 < text.Length && text[open + 1] == '%')
        {
            bool directive = open + 2 < text.Length && text[open + 2] == '@';
            int close = text.IndexOf("%>", open, StringComparison.Ordinal);
            if (close >= 0 && close < offset)
                return AspxCompletionContext.None; // the block ended before the caret

            return directive
                ? new AspxCompletionContext(AspxContextKind.Directive, new TextSpan(offset, 0), TagStart: open)
                : new AspxCompletionContext(AspxContextKind.Code, new TextSpan(offset, 0), TagStart: open);
        }

        // A closing tag or a comment has nothing to offer.
        if (open + 1 < text.Length && (text[open + 1] == '/' || text[open + 1] == '!'))
            return AspxCompletionContext.None;

        return ScanTag(text, open, offset);
    }

    private static AspxCompletionContext ScanTag(string text, int open, int offset)
    {
        int i = open + 1;

        int nameStart = i;
        while (i < offset && i < text.Length && IsNameChar(text[i]))
            i++;

        // A '<' followed by something that cannot start a tag name is a less-than operator, not
        // markup — the same call completion has to make in C#.
        if (i == nameStart && (i >= text.Length || !IsNameStart(text[i])))
            return AspxCompletionContext.None;

        var (prefix, name) = SplitQualifiedName(text[nameStart..i]);

        if (i >= offset)
        {
            int nameEnd = i;
            while (nameEnd < text.Length && IsNameChar(text[nameEnd]))
                nameEnd++;
            return new AspxCompletionContext(
                AspxContextKind.TagName, TextSpan.FromBounds(nameStart, nameEnd),
                prefix, name, TagStart: open);
        }

        // Past the tag name: walk attributes to the caret, tracking which one it lands in.
        string? attributeName = null;

        while (i < offset && i < text.Length)
        {
            char c = text[i];

            if (c == '>' )
                return AspxCompletionContext.None; // the tag closed before the caret

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '>')
                return AspxCompletionContext.None;

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '=')
            {
                i++;
                continue;
            }

            if (c is '"' or '\'')
            {
                int valueStart = i + 1;
                int valueEnd = text.IndexOf(c, valueStart);
                if (valueEnd < 0 || valueEnd >= offset)
                {
                    int end = valueEnd < 0 ? offset : valueEnd;
                    return new AspxCompletionContext(
                        AspxContextKind.AttributeValue,
                        TextSpan.FromBounds(valueStart, Math.Max(valueStart, end)),
                        prefix, name, attributeName, open);
                }
                i = valueEnd + 1;
                attributeName = null;
                continue;
            }

            int attrStart = i;
            while (i < text.Length && IsAttributeChar(text[i]))
                i++;
            if (i == attrStart)
            {
                i++;
                continue;
            }

            attributeName = text[attrStart..i];

            if (i >= offset)
            {
                int attrEnd = i;
                while (attrEnd < text.Length && IsAttributeChar(text[attrEnd]))
                    attrEnd++;
                return new AspxCompletionContext(
                    AspxContextKind.AttributeName, TextSpan.FromBounds(attrStart, attrEnd),
                    prefix, name, attributeName, open);
            }
        }

        return new AspxCompletionContext(
            AspxContextKind.AttributeName, new TextSpan(offset, 0), prefix, name, null, open);
    }

    private static (string? _prefix, string _name) SplitQualifiedName(string qualified)
    {
        int colon = qualified.IndexOf(':');
        return colon < 0 ? (null, qualified) : (qualified[..colon], qualified[(colon + 1)..]);
    }

    private static bool IsNameStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or ':';

    private static bool IsAttributeChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or ':';
}

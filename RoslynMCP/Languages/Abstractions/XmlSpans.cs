using System.Globalization;
using System.Text;
using Microsoft.Language.Xml;
using TextSpan = Microsoft.CodeAnalysis.Text.TextSpan;
using XmlSpan = Microsoft.Language.Xml.TextSpan;

namespace RoslynMCP.Languages;

/// <summary>
/// Turning a full-fidelity XML node into a range in the buffer, and its raw text into the string the
/// document actually declares.
/// </summary>
/// <remarks>
/// The parser carries its own <c>TextSpan</c>, so every span that leaves it has to be converted
/// before <c>LspConverters</c>, <c>SourceText.Lines</c> or a <c>TextEdit</c> can use it. Shared
/// rather than per-pack because the two sharp edges below are the same wherever XML is read, and
/// getting either wrong moves a squiggle or an edit onto the wrong characters.
/// </remarks>
internal static class XmlSpans
{
    public static TextSpan ToRoslyn(this XmlSpan span) => new(span.Start, span.Length);

    /// <summary>
    /// The characters an attribute's quotes enclose, as written.
    /// </summary>
    /// <remarks>
    /// Taken from the quote tokens rather than by narrowing the value node's span, because the value
    /// node spans its quotes and the closing one is not always there. In a buffer being typed into,
    /// <c>name="Half</c> has a real opening quote and a synthesized, zero-width closing one — so
    /// bounding by the tokens gives <c>Half</c>, where subtracting one from each end would give
    /// <c>Hal</c> and put every edit one character short.
    ///
    /// This is the range <em>as written</em>: a value carrying an entity reference spans more
    /// characters than <see cref="Decode"/> returns, which is why callers that rewrite a value in
    /// place compare this length against the decoded string's before editing.
    /// </remarks>
    public static TextSpan ValueSpan(this XmlAttributeSyntax attribute)
    {
        if (attribute.ValueNode is not { } node)
            return default;

        int from = node.StartQuoteToken?.Span.End ?? node.Span.Start;
        int to = node.EndQuoteToken?.Span.Start ?? node.Span.End;
        return to >= from ? TextSpan.FromBounds(from, to) : default;
    }

    /// <summary>The text between an element's tags, or an empty span between them when it has none.</summary>
    /// <remarks>
    /// Taken from the tags rather than from the content nodes so that an element with no content at
    /// all still yields the position where content would go — which is where completion inserts and
    /// where a diagnostic about an empty value belongs.
    /// </remarks>
    public static TextSpan ContentSpan(this XmlElementBaseSyntax element)
    {
        if (element is not XmlElementSyntax { StartTag: { } start, EndTag: { } end })
            return default;

        int from = start.Span.End;
        int to = end.Span.Start;
        return to >= from ? TextSpan.FromBounds(from, to) : default;
    }

    /// <summary>
    /// The string an attribute declares, with entity references resolved.
    /// </summary>
    /// <remarks>
    /// The parser is deliberately lossless — <c>Value</c> hands back <c>A&amp;amp;B</c> exactly as it
    /// is written, because a tree that decoded it could not reproduce the file. Callers that compare
    /// against a name from somewhere else need the decoded form: a resource key written
    /// <c>A&amp;amp;B</c> is looked up from C# as <c>A&amp;B</c>, and leaving it encoded means the
    /// lookup silently misses.
    /// </remarks>
    public static string DecodedValue(this XmlAttributeSyntax attribute) => Decode(attribute.Value);

    /// <inheritdoc cref="DecodedValue"/>
    public static string DecodedValue(this XmlElementBaseSyntax element) => Decode(element.Value);

    /// <summary>
    /// Resolves the five entity references XML predefines, plus numeric character references.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="System.Net.WebUtility.HtmlDecode"/>: that resolves the whole HTML
    /// entity set, so <c>&amp;nbsp;</c> — which an XML document without a DTD does not define and a
    /// parser would reject — would come back as a character rather than as the literal text it is.
    /// An unrecognized or malformed reference is left alone for the same reason.
    /// </remarks>
    public static string Decode(string? text)
    {
        if (text is not { Length: > 0 } || text.IndexOf('&') < 0)
            return text ?? string.Empty;

        var builder = new StringBuilder(text.Length);
        int index = 0;

        while (index < text.Length)
        {
            int amp = text.IndexOf('&', index);
            if (amp < 0)
            {
                builder.Append(text, index, text.Length - index);
                break;
            }

            builder.Append(text, index, amp - index);

            int semicolon = text.IndexOf(';', amp + 1);
            if (semicolon < 0)
            {
                builder.Append(text, amp, text.Length - amp);
                break;
            }

            string entity = text[(amp + 1)..semicolon];
            if (Resolve(entity) is { } resolved)
            {
                builder.Append(resolved);
                index = semicolon + 1;
                continue;
            }

            // Not something XML defines. Keep it verbatim and carry on from after the ampersand, so
            // a stray `&` in the text cannot swallow the rest of the string.
            builder.Append('&');
            index = amp + 1;
        }

        return builder.ToString();
    }

    private static string? Resolve(string entity)
    {
        switch (entity)
        {
            case "lt": return "<";
            case "gt": return ">";
            case "amp": return "&";
            case "apos": return "'";
            case "quot": return "\"";
        }

        if (entity is not ['#', .. var digits] || digits.Length == 0)
            return null;

        bool hex = digits[0] is 'x' or 'X';
        if (hex)
            digits = digits[1..];

        if (digits.Length == 0)
            return null;

        return int.TryParse(
                   digits,
                   hex ? NumberStyles.HexNumber : NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out int code)
               && code is >= 0 and <= 0x10FFFF
            ? char.ConvertFromUtf32(code)
            : null;
    }
}

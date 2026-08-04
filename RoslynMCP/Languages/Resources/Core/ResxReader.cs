using System.Collections.Immutable;
using System.Text;
using System.Xml;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMCP.Languages.Resources.Core;

/// <summary>The string entries one <c>.resx</c> declares, and the keys it declares twice.</summary>
internal readonly record struct ResxContents(
    ImmutableDictionary<string, ResourceEntry> Entries, ImmutableArray<string> DuplicateKeys);

/// <summary>
/// Reads a <c>.resx</c> into a key table whose spans point back at the exact characters in the
/// buffer.
/// </summary>
/// <remarks>
/// <see cref="XmlReader"/> rather than <c>XDocument</c>, on three counts. <c>XDocument.Save</c> is a
/// whole-file rewrite where what is needed is a range edit against a buffer that may never be
/// saved; <see cref="IXmlLineInfo"/> on an <c>XAttribute</c> reports the position of the attribute
/// <em>name</em> and no end position at all; and <c>XDocument</c> normalizes entities, so
/// <c>A&amp;amp;B</c> comes back four characters shorter than the text on disk and every span
/// derived from it is wrong. <see cref="XmlReader.ReadAttributeValue"/> is the one API that
/// positions the reader on the attribute's value node instead of its name.
/// </remarks>
internal static class ResxReader
{
    private static readonly XmlReaderSettings s_settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        IgnoreComments = true,
        CloseInput = true,
    };

    public static ResxContents Read(SourceText text)
    {
        var entries = ImmutableDictionary.CreateBuilder<string, ResourceEntry>(StringComparer.Ordinal);
        var duplicates = ImmutableArray.CreateBuilder<string>();

        try
        {
            using var reader = XmlReader.Create(new StringReader(text.ToString()), s_settings);
            var info = reader as IXmlLineInfo;

            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element
                    || !reader.LocalName.Equals("data", StringComparison.Ordinal))
                {
                    continue;
                }

                if (ReadData(reader, info, text) is not { } entry)
                    continue;

                if (entries.ContainsKey(entry.Key))
                {
                    if (!duplicates.Contains(entry.Key))
                        duplicates.Add(entry.Key);
                    continue;
                }

                entries.Add(entry.Key, entry);
            }
        }
        catch (XmlException)
        {
            // A half-typed buffer is the normal state of an open document; report the entries
            // that were read before the text stopped being XML.
        }

        return new ResxContents(entries.ToImmutable(), duplicates.ToImmutable());
    }

    private static ResourceEntry? ReadData(XmlReader reader, IXmlLineInfo? info, SourceText text)
    {
        string? key = null;
        TextSpan keySpan = default;
        bool typed = false;
        int depth = reader.Depth;
        bool empty = reader.IsEmptyElement;

        if (reader.MoveToFirstAttribute())
        {
            do
            {
                switch (reader.LocalName)
                {
                    case "name":
                        key = reader.Value;
                        break;

                    // A ResXFileRef or a serialized object. The key is still a key — a rename has
                    // to move it and a missing-key diagnostic must not fire on it — but there is no
                    // string to show, so the value stays null.
                    case "type":
                    case "mimetype":
                        typed = true;
                        break;
                }
            }
            while (reader.MoveToNextAttribute());

            // Spanning the name is left until the attribute walk is finished, because it leaves the
            // reader on a value node rather than on an attribute and MoveToElement is the only
            // documented way back out.
            if (key is { Length: > 0 } && reader.MoveToAttribute("name"))
                keySpan = AttributeValueSpan(reader, info, text, key);

            reader.MoveToElement();
        }

        if (key is not { Length: > 0 })
            return null;

        string? value = null;
        string? comment = null;
        TextSpan valueSpan = default;

        if (!empty)
        {
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                    break;

                if (reader.NodeType != XmlNodeType.Element || reader.Depth != depth + 1)
                    continue;

                switch (reader.LocalName)
                {
                    case "value":
                        ReadElementText(reader, info, text, out value, out valueSpan);
                        break;
                    case "comment":
                        ReadElementText(reader, info, text, out comment, out _);
                        break;
                }
            }
        }

        return new ResourceEntry(key, typed ? null : value, comment, keySpan, valueSpan);
    }

    /// <summary>
    /// The span of the attribute value the reader is positioned on, or <see langword="default"/>
    /// when it cannot be pinned down exactly.
    /// </summary>
    /// <remarks>
    /// More than one value node means the name carries an entity reference, and the decoded text no
    /// longer lines up with the file. The slice check catches the rest of that family — a resolved
    /// <c>&amp;amp;</c> arrives as a single node whose length is four characters short of the
    /// source. Either way the caller gets nothing and declines to rename the key: no rename beats a
    /// rename applied to the wrong range.
    /// </remarks>
    private static TextSpan AttributeValueSpan(
        XmlReader reader, IXmlLineInfo? info, SourceText text, string value)
    {
        if (info is null)
            return default;

        int start = -1;
        int nodes = 0;

        while (reader.ReadAttributeValue())
        {
            if (nodes == 0)
                start = Offset(text, info);
            nodes++;
        }

        return nodes == 1 && Matches(text, start, value)
            ? new TextSpan(start, value.Length)
            : default;
    }

    private static void ReadElementText(
        XmlReader reader, IXmlLineInfo? info, SourceText text, out string? value, out TextSpan span)
    {
        span = default;

        if (reader.IsEmptyElement)
        {
            value = string.Empty;
            return;
        }

        value = null;

        int depth = reader.Depth;
        var builder = new StringBuilder();
        int start = -1;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                // LinePosition on an end tag points at the name, past the "</".
                int end = info is null ? -1 : Offset(text, info) - 2;
                if (start < 0)
                    start = end;

                if (start >= 0 && end >= start)
                    span = TextSpan.FromBounds(start, end);

                value = builder.ToString();
                return;
            }

            switch (reader.NodeType)
            {
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.SignificantWhitespace:
                case XmlNodeType.Whitespace:
                    if (start < 0 && info is not null)
                        start = Offset(text, info);
                    builder.Append(reader.Value);
                    break;
            }
        }
    }

    /// <summary>The 1-based line and column the reader reports, as an offset into the buffer.</summary>
    private static int Offset(SourceText text, IXmlLineInfo info)
    {
        if (!info.HasLineInfo())
            return -1;

        int line = info.LineNumber - 1;
        int character = info.LinePosition - 1;

        if (line < 0 || line >= text.Lines.Count || character < 0)
            return -1;

        var span = text.Lines[line].SpanIncludingLineBreak;
        return character > span.Length ? -1 : text.Lines.GetPosition(new LinePosition(line, character));
    }

    private static bool Matches(SourceText text, int start, string value)
    {
        if (start < 0 || start + value.Length > text.Length)
            return false;

        for (int i = 0; i < value.Length; i++)
        {
            if (text[start + i] != value[i])
                return false;
        }

        return true;
    }
}

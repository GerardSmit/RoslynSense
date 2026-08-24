using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Language.Xml;
using TextSpan = Microsoft.CodeAnalysis.Text.TextSpan;

namespace RoslynMCP.Languages.Resources.Core;

/// <summary>The string entries one <c>.resx</c> declares, and the keys it declares twice.</summary>
internal readonly record struct ResxContents(
    ImmutableDictionary<string, ResourceEntry> Entries, ImmutableArray<string> DuplicateKeys);

/// <summary>
/// Reads a <c>.resx</c> into a key table whose spans point back at the exact characters in the
/// buffer.
/// </summary>
/// <remarks>
/// A full-fidelity parse rather than <see cref="System.Xml.XmlReader"/> or <c>XDocument</c>. Every
/// character of the source is in the tree, so a node's span already <em>is</em> the range in the
/// buffer and nothing has to be rebuilt from line and column. That matters most where the two older
/// approaches were weakest: <c>XDocument</c> normalizes entities, so <c>A&amp;amp;B</c> comes back
/// four characters shorter than the text on disk and every span derived from it is wrong; and an
/// <see cref="System.Xml.XmlReader"/> stops at the first malformation, which in an open document is
/// wherever the caret is. This parser is error-tolerant, so a half-typed buffer — the normal state
/// of a file being edited — still yields every entry, including the ones after the break.
/// </remarks>
internal static class ResxReader
{
    public static ResxContents Read(SourceText text)
    {
        var entries = ImmutableDictionary.CreateBuilder<string, ResourceEntry>(StringComparer.Ordinal);
        var duplicates = ImmutableArray.CreateBuilder<string>();

        foreach (var element in Parser.ParseText(text.ToString()).DescendantNodes().OfType<XmlElementSyntax>())
        {
            if (!element.Name.Equals("data", StringComparison.Ordinal))
                continue;

            if (ReadData(element) is not { } entry)
                continue;

            if (entries.ContainsKey(entry.Key))
            {
                if (!duplicates.Contains(entry.Key))
                    duplicates.Add(entry.Key);
                continue;
            }

            entries.Add(entry.Key, entry);
        }

        return new ResxContents(entries.ToImmutable(), duplicates.ToImmutable());
    }

    private static ResourceEntry? ReadData(XmlElementSyntax data)
    {
        string? key = null;
        TextSpan keySpan = default;

        // A ResXFileRef or a serialized object. The key is still a key — a rename has to move it and
        // a missing-key diagnostic must not fire on it — but there is no string to show, so the
        // value stays null.
        bool typed = false;

        foreach (var attribute in data.Attributes)
        {
            switch (attribute.Name)
            {
                case "name":
                    // Decoded, because this key is compared against the one a `GetString` call in C#
                    // passes. The span stays raw — it is where the characters are.
                    key = attribute.Value;
                    keySpan = attribute.ValueSpan.ToRoslynSpan();
                    break;

                case "type":
                case "mimetype":
                    typed = true;
                    break;
            }
        }

        if (key is not { Length: > 0 })
            return null;

        string? value = null;
        string? comment = null;
        TextSpan valueSpan = default;

        foreach (var child in data.Elements)
        {
            switch (child.Name)
            {
                case "value":
                    value = child.Value;
                    valueSpan = child.ContentSpan.ToRoslynSpan();
                    break;
                case "comment":
                    comment = child.Value;
                    break;
            }
        }

        return new ResourceEntry(key, typed ? null : value, comment, keySpan, valueSpan);
    }
}

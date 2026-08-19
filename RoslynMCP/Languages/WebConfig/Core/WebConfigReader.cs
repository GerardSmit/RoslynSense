using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Language.Xml;
using TextSpan = Microsoft.CodeAnalysis.Text.TextSpan;

namespace RoslynMCP.Languages.WebConfig.Core;

/// <summary>
/// Reads the named settings out of a <c>.config</c> file, with spans that point at the exact
/// characters in the buffer.
/// </summary>
/// <remarks>
/// <para>
/// A full-fidelity parse, for the reasons <c>ResxReader</c> and <c>DbmlReader</c> set out: every
/// character of the source is a node, so a span already <em>is</em> a range in the buffer, and the
/// parse is error-tolerant, so a file being typed into still yields the entries around the caret
/// rather than stopping at the first malformation. <c>XDocument</c> — which is what this read used
/// to go through, on the markup side — normalizes entities, so a key written <c>A&amp;amp;B</c>
/// comes back four characters shorter than the text on disk and every span derived from it lands
/// in the wrong place.
/// </para>
/// <para>
/// Deliberately shallow about what the runtime would do with the file: <c>configSource</c> and
/// <c>file</c> redirection to another document, and build-time XDT transforms, are both out. What
/// is here is what the file itself declares — including entries inside a <c>&lt;location&gt;</c>,
/// which apply to one path rather than to the whole application but are still declarations of the
/// name.
/// </para>
/// </remarks>
internal static class WebConfigReader
{
    public static ImmutableArray<WebConfigEntry> Read(SourceText text, string filePath) =>
        Read(Parser.ParseText(text.ToString()), filePath);

    public static ImmutableArray<WebConfigEntry> Read(XmlDocumentSyntax root, string filePath)
    {
        var entries = ImmutableArray.CreateBuilder<WebConfigEntry>();

        foreach (var element in root.DescendantNodes().OfType<XmlElementBaseSyntax>())
        {
            var section = LocalName(element) switch
            {
                "appSettings" => WebConfigSection.AppSettings,
                "connectionStrings" => WebConfigSection.ConnectionStrings,
                _ => (WebConfigSection?)null,
            };

            if (section is not { } named)
                continue;

            foreach (var add in element.Elements)
            {
                if (LocalName(add) != "add")
                    continue;

                if (ReadEntry(add, named, filePath) is { } entry)
                    entries.Add(entry);
            }
        }

        return entries.ToImmutable();
    }

    private static WebConfigEntry? ReadEntry(
        XmlElementBaseSyntax add, WebConfigSection section, string filePath)
    {
        string naming = section == WebConfigSection.AppSettings ? "key" : "name";

        if (AttributeNode(add, naming) is not { } name)
            return null;

        // Decoded, because this name is compared against the one a C# literal or a markup
        // expression passes. The span stays raw — it is where the characters are.
        string decoded = name.DecodedValue();

        if (decoded.Length == 0)
            return null;

        return new WebConfigEntry(
            decoded,
            Attribute(add, section == WebConfigSection.AppSettings ? "value" : "connectionString"),
            section == WebConfigSection.ConnectionStrings ? Attribute(add, "providerName") : null,
            section,
            filePath,
            NameSpan(name, decoded));
    }

    /// <summary>
    /// The attribute value's own characters, or <see langword="default"/> when an entity puts the
    /// decoded name and the text out of step.
    /// </summary>
    /// <remarks>
    /// A range that is merely close is worse than none: it is what a peek highlights, what a lens
    /// sits on and what a rename would rewrite. The comparison is the one <see cref="XmlSpans"/>
    /// documents — the written form spans more characters than the decoded string.
    /// </remarks>
    private static TextSpan NameSpan(XmlAttributeSyntax attribute, string decoded)
    {
        var span = attribute.ValueSpan();
        return span.Length == decoded.Length ? span : default;
    }

    private static XmlAttributeSyntax? AttributeNode(XmlElementBaseSyntax element, string name)
    {
        foreach (var attribute in element.Attributes)
        {
            if (string.Equals(attribute.NameNode.LocalName, name, StringComparison.OrdinalIgnoreCase))
                return attribute;
        }

        return null;
    }

    private static string? Attribute(XmlElementBaseSyntax element, string name) =>
        AttributeNode(element, name) is { } attribute ? attribute.DecodedValue() : null;

    /// <summary>A name without its prefix, so a config written with one still reads.</summary>
    private static string LocalName(XmlElementBaseSyntax element) =>
        element.NameNode?.LocalName ?? string.Empty;
}

using Microsoft.CodeAnalysis.Text;
using Microsoft.Language.Xml;
using TextSpan = Microsoft.CodeAnalysis.Text.TextSpan;

namespace RoslynMCP.Services.Packages;

/// <summary>
/// Where something is written in a config file, as 0-based line and character bounds.
/// </summary>
/// <remarks>
/// A squiggle over a whole line says "something on this line is wrong" and leaves the reader to work
/// out what. The thing actually wrong about a stale redirect is the eight characters of one
/// attribute value, and pointing at those is the difference between a warning that reads as an
/// answer and one that reads as a nudge.
/// </remarks>
public sealed record ConfigSpan(int Line, int Character, int EndLine, int EndCharacter)
{
    /// <summary>
    /// The same span in the coordinates a client understands, or <see langword="null"/> when the
    /// node it came from was not in the document.
    /// </summary>
    /// <remarks>
    /// A <see langword="default"/> span is how the parser reports a node it synthesized rather than
    /// read — an attribute half-typed, a value whose closing quote is not there yet — and there is
    /// no position in the file to point at for one.
    /// </remarks>
    public static ConfigSpan? From(SourceText text, TextSpan span)
    {
        if (span == default || span.End > text.Length)
            return null;

        var start = text.Lines.GetLinePosition(span.Start);
        var end = text.Lines.GetLinePosition(span.End);

        return new ConfigSpan(start.Line, start.Character, end.Line, end.Character);
    }
}

/// <summary>
/// The <c>assemblyBinding</c> section, as the analysis and the rewrite both have to find it.
/// </summary>
/// <remarks>
/// Shared because the two have to agree about which element is which: a finding names a redirect
/// the fix then has to find again, and two different notions of "the binding section" would be a
/// fix that quietly edits something else. By local name, because the assembly binding namespace is
/// bound to a prefix in plenty of hand-written configs — <c>&lt;asm:dependentAssembly&gt;</c> — and
/// a lookup by qualified name misses every one of them.
/// </remarks>
internal static class ConfigXml
{
    public static XmlElementBaseSyntax? Section(XmlElementBaseSyntax? configuration) =>
        configuration?
            .GetElementByLocalName("runtime")?
            .GetElementByLocalName("assemblyBinding");

    /// <inheritdoc cref="Section(XmlElementBaseSyntax)"/>
    public static XmlElementBaseSyntax? Section(XmlDocumentSyntax document) =>
        Section(document.RootSyntax);
}

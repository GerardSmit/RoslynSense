using Microsoft.CodeAnalysis.Text;
using Microsoft.Language.Xml;
using TextSpan = Microsoft.CodeAnalysis.Text.TextSpan;

namespace RoslynMCP.Languages.MsBuild.Core;

/// <summary>
/// What the caret is on, as a set of overlapping facts rather than one label.
/// </summary>
/// <remarks>
/// Flags rather than an enum because the real states combine, and the combinations are what
/// completion dispatches on. <c>Attribute | Name</c> and <c>Attribute | Value</c> are different
/// questions; so is <c>Element | Attributes</c> — inside a tag's attribute region but on no
/// attribute in particular — which is exactly where a new attribute name is typed and which a
/// single-valued kind cannot express at all.
/// </remarks>
[Flags]
internal enum MsBuildLocationFlags
{
    None = 0,

    /// <summary>On an element.</summary>
    Element = 1 << 0,

    /// <summary>On an attribute.</summary>
    Attribute = 1 << 1,

    /// <summary>On the name half of whatever it is on.</summary>
    Name = 1 << 2,

    /// <summary>On the value half — an attribute's quoted text, or an element's content.</summary>
    Value = 1 << 3,

    /// <summary>Inside a start tag, between the name and the closing angle bracket.</summary>
    Attributes = 1 << 4,

    /// <summary>On whitespace, which is a place a new element can be typed rather than a gap.</summary>
    Whitespace = 1 << 5,

    /// <summary>Inside a comment or a CDATA section, where nothing is offered.</summary>
    Comment = 1 << 6,

    /// <summary>The element under the caret is one the parser could not name — <c>&lt;&gt;</c>.</summary>
    Invalid = 1 << 7,
}

/// <summary>
/// Where a completion's replacement needs a space so it does not weld itself to its neighbour.
/// </summary>
/// <remarks>
/// Typing an attribute name in <c>&lt;PackageReference |/&gt;</c> has to insert <c>Include=""</c>
/// with nothing added, but in <c>&lt;PackageReference Include="x"|/&gt;</c> it needs a leading space
/// or the result is <c>Include="x"Version=""</c>. The caret alone does not say which; its neighbours
/// do.
/// </remarks>
internal enum MsBuildPadding
{
    None,
    Leading,
    Trailing,
}

/// <summary>The caret, resolved against the tree.</summary>
/// <param name="Flags">What the caret is on. <see cref="MsBuildLocationFlags.None"/> means nothing
/// worth offering — inside a comment, or in a file the pack does not own.</param>
/// <param name="Element">The element the caret is within, kept as the live node rather than
/// flattened: padding, a sibling attribute's value, and whether the element is even well-formed all
/// need it.</param>
/// <param name="Attribute">The attribute the caret is on, when it is on one.</param>
/// <param name="ElementName">The element's name, or empty.</param>
/// <param name="AttributeName">The attribute's name, or null.</param>
/// <param name="Path">The element path from the root — <c>Project/ItemGroup/PackageReference</c>.</param>
/// <param name="ReplaceSpan">The range a completion replaces. The whole existing value, never the
/// prefix before the caret.</param>
/// <param name="Padding">Whether an inserted attribute needs a space beside it.</param>
internal readonly record struct MsBuildContext(
    MsBuildLocationFlags Flags,
    XmlElementBaseSyntax? Element,
    XmlAttributeSyntax? Attribute,
    string ElementName,
    string? AttributeName,
    string Path,
    TextSpan ReplaceSpan,
    MsBuildPadding Padding)
{
    public static MsBuildContext None => new(
        MsBuildLocationFlags.None, null, null, string.Empty, null, string.Empty, default,
        MsBuildPadding.None);

    public bool Is(MsBuildLocationFlags flags) => (Flags & flags) == flags;

    /// <summary>The value of a sibling attribute on the same tag.</summary>
    /// <remarks>
    /// The reason <see cref="Element"/> is carried. Completing <c>Version="…"</c> needs the
    /// <c>Include=</c> beside it, on a tag that is still being typed and has no closing bracket yet,
    /// which no amount of walking the finished tree can supply.
    /// </remarks>
    public string? Sibling(string name) => Empty(Element?.GetAttributeValue(name));

    private static string? Empty(string? value) => value is { Length: > 0 } ? XmlSpans.Decode(value) : null;
}

internal static class MsBuildContextResolver
{
    /// <summary>
    /// Resolves a caret offset against a parsed document.
    /// </summary>
    /// <param name="document">The parsed buffer.</param>
    /// <param name="offset">The caret, as an offset into the buffer.</param>
    /// <remarks>
    /// The character that opened the completion list is already in the buffer and already behind the
    /// caret, so a replacement that started at the caret would leave it there — typing <c>net8.</c>
    /// and accepting <c>net8.0</c> would give <c>net8.net8.0</c>. Nothing here compensates for that,
    /// because nothing needs to: every replacement is the span of the <em>whole</em> value the caret
    /// is in, which already contains the trigger. That holds only while every trigger character is
    /// one the value can contain — <c>.</c>, <c>/</c>, <c>\</c> — or one that opens the value and
    /// sits outside it, which is what <c>&lt;</c>, <c>"</c> and <c>'</c> do.
    /// </remarks>
    public static MsBuildContext Resolve(MsBuildDocument document, int offset)
    {
        var text = document.Text;
        if (offset < 0 || offset > text.Length)
            return MsBuildContext.None;

        // At the very end of the buffer every synthesized closing token sits at the same zero-width
        // position, and the lookup cannot tell which of them the caret belongs to — it answers with
        // the document. One character back is unambiguous and is inside the same node, which matters
        // because the end of the buffer is exactly where an unterminated `Version="` is typed.
        int probe = offset == text.Length && offset > 0 ? offset - 1 : offset;

        var node = SyntaxLocator.FindNode(
            document.Root, probe, descendIntoChildren: null, includeTrivia: true, excludeTerminal: false);

        if (node is null)
            return MsBuildContext.None;

        if (InsideComment(node))
            return new MsBuildContext(
                MsBuildLocationFlags.Comment, null, null, string.Empty, null, string.Empty, default,
                MsBuildPadding.None);

        XmlAttributeSyntax? attribute = null;
        XmlElementBaseSyntax? element = null;
        bool whitespace = node is SyntaxTrivia
        {
            Kind: SyntaxKind.WhitespaceTrivia or SyntaxKind.EndOfLineTrivia,
        };

        for (var current = node; current is not null; current = current.Parent)
        {
            attribute ??= current as XmlAttributeSyntax;
            element ??= current as XmlElementBaseSyntax;
        }

        // `<<PackageReference />` parses as an element the parser could not name, wrapping the one
        // that was meant. Two positions land in it and both have to answer about the inner element:
        // the caret directly on the unnamed wrapper, which retargets, and the caret deeper inside —
        // on an attribute of the real element — which already resolves correctly and only needs
        // saying that the tag it is in is malformed.
        bool invalid = false;
        for (var current = element; current is not null; current = current.ParentElement)
        {
            if (NameOf(current).Length == 0)
            {
                invalid = true;
                break;
            }
        }

        if (element is XmlElementSyntax { Name: "" } unnamed
            && unnamed.Elements.FirstOrDefault() is { } inner)
        {
            element = inner;
        }

        string elementName = NameOf(element);
        string path = PathOf(element);

        if (attribute is not null)
        {
            var valueSpan = attribute.ValueSpan();
            bool onValue = attribute.ValueNode is not null && Touches(valueSpan, offset);

            return new MsBuildContext(
                MsBuildLocationFlags.Attribute
                | (onValue ? MsBuildLocationFlags.Value : MsBuildLocationFlags.Name)
                | (invalid ? MsBuildLocationFlags.Invalid : 0),
                element,
                attribute,
                elementName,
                attribute.Name,
                path,
                onValue ? valueSpan : attribute.NameNode?.Span.ToRoslyn() ?? default,
                MsBuildPadding.None);
        }

        if (element is null)
            return MsBuildContext.None;

        // Inside the start tag but on no attribute: where a new attribute name goes. Strictly
        // inside, because both ends belong to something else — the element name before it, and the
        // content after the closing bracket. A caret one past the `>` of `<LangVersion>` is in the
        // element's text, and reading it as the attribute region offers attribute names where a
        // value goes.
        if (StartTagAttributeRegion(element) is { } region && Within(region, offset))
        {
            return new MsBuildContext(
                MsBuildLocationFlags.Element | MsBuildLocationFlags.Attributes
                | (invalid ? MsBuildLocationFlags.Invalid : 0),
                element, null, elementName, null, path,
                new TextSpan(offset, 0),
                PaddingFor(document.Text, offset));
        }

        // A tag that is still being typed has no end tag yet, and the parser answers about the
        // nearest one that does — the caret in `<PropertyGroup><LangVersion>|` is reported as
        // whitespace inside the PropertyGroup. That is the state completion runs in on every
        // keystroke, so the text is read directly rather than trusted to the tree.
        if (whitespace && MsBuildMarkupScan.Scan(text, offset) is { } typed)
            return Typed(text, element, path, typed, invalid);

        // Content, including the whitespace between two children. Whitespace is an affirmative
        // answer here, not a gap: an empty line inside a <PropertyGroup> is where the next property
        // is typed, and offering nothing there is the difference between the feature working and
        // appearing not to.
        var content = element.ContentSpan();
        var flags = MsBuildLocationFlags.Element | MsBuildLocationFlags.Value
                    | (whitespace ? MsBuildLocationFlags.Whitespace : 0)
                    | (invalid ? MsBuildLocationFlags.Invalid : 0);

        return new MsBuildContext(
            flags, element, null, elementName, null, path,
            whitespace ? new TextSpan(offset, 0) : content,
            MsBuildPadding.None);
    }

    /// <summary>
    /// The answer for a caret the parser could only place approximately.
    /// </summary>
    /// <remarks>
    /// Two of them, and the difference is a newline. Content on the same line as the start tag is
    /// the element's value — <c>&lt;LangVersion&gt;|</c> — and content on a later line is where a
    /// child goes, which is what an unclosed <c>&lt;PropertyGroup&gt;</c> is. Both retarget the
    /// element, because the one the parser named is the wrong one in exactly this state.
    /// </remarks>
    private static MsBuildContext Typed(
        SourceText text, XmlElementBaseSyntax element, string parentPath, MsBuildMarkup typed, bool invalid)
    {
        var flags = MsBuildLocationFlags.Element
                    | (typed.OnName ? MsBuildLocationFlags.Name : MsBuildLocationFlags.Value)
                    | (typed.Whitespace ? MsBuildLocationFlags.Whitespace : 0)
                    | (invalid ? MsBuildLocationFlags.Invalid : 0);

        // On a name the element is the one being typed *into*, so the parser's answer is already
        // the right one; on a value it is the tag the scan found.
        string name = typed.OnName ? NameOf(element) : typed.Name;
        string path = typed.OnName || typed.Name.Equals(NameOf(element), StringComparison.OrdinalIgnoreCase)
            ? parentPath
            : parentPath.Length > 0 ? parentPath + "/" + typed.Name : typed.Name;

        return new MsBuildContext(
            flags, element, null, name, null, path, typed.Span, MsBuildPadding.None);
    }

    /// <summary>Inclusive at both ends: a caret at either edge of a value is still in it.</summary>
    private static bool Touches(TextSpan span, int offset) =>
        offset >= span.Start && offset <= span.End;

    /// <summary>Exclusive at both ends, for a region whose edges belong to its neighbours.</summary>
    private static bool Within(TextSpan span, int offset) =>
        offset > span.Start && offset < span.End;

    private static bool InsideComment(SyntaxNode node)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            if (current.Kind is SyntaxKind.XmlComment or SyntaxKind.XmlCDataSection)
                return true;
        }

        return node is SyntaxTrivia { Kind: SyntaxKind.XmlComment };
    }

    private static string NameOf(XmlElementBaseSyntax? element) => element?.Name ?? string.Empty;

    /// <summary>The element path from the root, slash-separated.</summary>
    /// <remarks>
    /// A plain string rather than a matcher type. Every question the pack asks of it is a suffix
    /// test — "is this a PackageReference in an ItemGroup" — which a string answers directly.
    /// </remarks>
    private static string PathOf(XmlElementBaseSyntax? element)
    {
        var names = new List<string>();
        for (var current = element; current is not null; current = current.ParentElement)
        {
            string name = NameOf(current);
            if (name.Length > 0)
                names.Add(name);
        }

        names.Reverse();
        return string.Join('/', names);
    }

    /// <summary>The region of a start tag after the element name, where attributes live.</summary>
    private static TextSpan? StartTagAttributeRegion(XmlElementBaseSyntax element)
    {
        switch (element)
        {
            case XmlElementSyntax { StartTag: { } start }:
                return Region(start.NameNode?.Span.End, start.Span.End);
            case XmlEmptyElementSyntax empty:
                return Region(empty.NameNode?.Span.End, empty.Span.End);
            default:
                return null;
        }

        static TextSpan? Region(int? from, int to) =>
            from is { } start && to >= start ? TextSpan.FromBounds(start, to) : null;
    }

    private static MsBuildPadding PaddingFor(SourceText text, int offset)
    {
        bool spaceBefore = offset > 0 && char.IsWhiteSpace(text[offset - 1]);
        bool spaceAfter = offset < text.Length && char.IsWhiteSpace(text[offset]);

        return (spaceBefore, spaceAfter) switch
        {
            (true, _) => MsBuildPadding.None,
            (false, true) => MsBuildPadding.Leading,
            _ => MsBuildPadding.Leading,
        };
    }
}

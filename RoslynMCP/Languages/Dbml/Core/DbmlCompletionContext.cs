using Microsoft.Language.Xml;
using TextSpan = Microsoft.CodeAnalysis.Text.TextSpan;

namespace RoslynMCP.Languages.Dbml.Core;

/// <summary>Which of the three completion sites a caret is in.</summary>
internal enum DbmlSite
{
    /// <summary>Nothing worth offering — inside a comment, or in text.</summary>
    None,

    /// <summary>Where an element name goes: just after a <c>&lt;</c>, or on whitespace in content.</summary>
    ElementName,

    /// <summary>Inside a start tag, on no attribute: where a new attribute name goes.</summary>
    AttributeName,

    /// <summary>Inside an attribute's quotes.</summary>
    AttributeValue,
}

/// <summary>The caret in a <c>.dbml</c>, resolved against the tree.</summary>
/// <param name="ElementName">The element the caret is in or on, or empty.</param>
/// <param name="ParentName">The element containing that one, which is what decides whether a
/// <c>&lt;Column&gt;</c> is legal where the caret is.</param>
/// <param name="AttributeName">The attribute the caret is on, when it is on one.</param>
/// <param name="ReplaceSpan">What a completion replaces — the whole existing value, never the prefix
/// before the caret, so accepting an item does not double the characters already typed.</param>
internal readonly record struct DbmlCompletionContext(
    DbmlSite Site,
    XmlElementBaseSyntax? Element,
    string ElementName,
    string ParentName,
    string? AttributeName,
    TextSpan ReplaceSpan)
{
    public static DbmlCompletionContext None =>
        new(DbmlSite.None, null, string.Empty, string.Empty, null, default);
}

/// <summary>
/// Where the caret is in a <c>.dbml</c>, for completion.
/// </summary>
/// <remarks>
/// Its own resolver rather than the MSBuild pack's, which is doing the same job on the same parser.
/// The two files' shapes are what differ: a project file's questions are about a path from the root
/// and about the region between attributes, while a model's are about an element's immediate parent
/// and about the attributes of a fixed vocabulary. Sharing would mean a context carrying both packs'
/// questions, and neither could be read without knowing which half applied.
/// </remarks>
internal static class DbmlCompletionResolver
{
    public static DbmlCompletionContext Resolve(DbmlDocument document, int offset)
    {
        var text = document.Text;

        if (offset < 0 || offset > text.Length)
            return DbmlCompletionContext.None;

        // At the very end of the buffer every synthesized closing token sits at the same zero-width
        // position and the lookup cannot tell which the caret belongs to. One character back is
        // unambiguous and is inside the same node — which is where an unterminated `Type="` is typed.
        int probe = offset == text.Length && offset > 0 ? offset - 1 : offset;

        var node = SyntaxLocator.FindNode(
            document.Root, probe, descendIntoChildren: null, includeTrivia: true, excludeTerminal: false);

        if (node is null || InsideComment(node))
            return DbmlCompletionContext.None;

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

        if (attribute is not null)
        {
            var valueSpan = attribute.ValueSpan();
            bool onValue = attribute.ValueNode is not null
                           && offset >= valueSpan.Start && offset <= valueSpan.End;

            return new DbmlCompletionContext(
                onValue ? DbmlSite.AttributeValue : DbmlSite.AttributeName,
                element,
                NameOf(element),
                NameOf(element?.ParentElement),
                attribute.Name,
                onValue ? valueSpan : attribute.NameNode?.Span.ToRoslyn() ?? default);
        }

        if (element is null)
            return DbmlCompletionContext.None;

        // On the element's own name, which is where a half-typed `<Col` sits. The parent is what
        // decides what may be typed there, so it — not the element being typed — is the context.
        if (NameSpanOf(element) is { } nameSpan && offset >= nameSpan.Start && offset <= nameSpan.End)
        {
            return new DbmlCompletionContext(
                DbmlSite.ElementName, element, NameOf(element),
                NameOf(element.ParentElement), null, nameSpan);
        }

        // Strictly inside the start tag, because both ends belong to something else — the element
        // name before it, and the content after the closing bracket.
        if (AttributeRegion(element) is { } region && offset > region.Start && offset < region.End)
        {
            return new DbmlCompletionContext(
                DbmlSite.AttributeName, element, NameOf(element),
                NameOf(element.ParentElement), null, new TextSpan(offset, 0));
        }

        // Content. Whitespace is an affirmative answer rather than a gap: a blank line inside a
        // <Type> is where the next <Column> is typed, and offering nothing there is the difference
        // between the feature working and appearing not to. The element the caret is inside is the
        // parent of what would be typed, which is why both names come from the same node here.
        if (whitespace || offset == 0)
        {
            return new DbmlCompletionContext(
                DbmlSite.ElementName, element, NameOf(element), NameOf(element),
                null, new TextSpan(offset, 0));
        }

        return DbmlCompletionContext.None;
    }

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

    private static TextSpan? NameSpanOf(XmlElementBaseSyntax element) => element switch
    {
        XmlElementSyntax { StartTag.NameNode: { } name } => name.Span.ToRoslyn(),
        XmlEmptyElementSyntax { NameNode: { } name } => name.Span.ToRoslyn(),
        _ => null,
    };

    private static TextSpan? AttributeRegion(XmlElementBaseSyntax element) => element switch
    {
        XmlElementSyntax { StartTag: { } start } =>
            Region(start.NameNode?.Span.End, start.Span.End),
        XmlEmptyElementSyntax empty => Region(empty.NameNode?.Span.End, empty.Span.End),
        _ => null,
    };

    private static TextSpan? Region(int? from, int to) =>
        from is { } start && to >= start ? TextSpan.FromBounds(start, to) : null;
}

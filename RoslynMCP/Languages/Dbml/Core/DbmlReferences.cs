using Microsoft.Language.Xml;
using TextSpan = Microsoft.CodeAnalysis.Text.TextSpan;

namespace RoslynMCP.Languages.Dbml.Core;

/// <summary>What an attribute value in a <c>.dbml</c> names.</summary>
internal enum DbmlReferenceKind
{
    /// <summary>A CLR type — <c>&lt;Column Type="System.Int32"&gt;</c>. Lives in C#.</summary>
    ClrType,

    /// <summary>The entity class at the other end of an association. Lives in this file.</summary>
    ModelType,

    /// <summary>A column of the type the element is written in.</summary>
    ThisKeyColumn,

    /// <summary>A column of the type the association points at.</summary>
    OtherKeyColumn,
}

/// <summary>One name written inside an attribute value, and what it refers to.</summary>
/// <param name="Span">The name's own characters — not the whole attribute. A composite key is a
/// comma-separated list and each column in it is a reference of its own.</param>
internal readonly record struct DbmlReference(
    DbmlReferenceKind Kind, string Name, TextSpan Span, XmlElementBaseSyntax Element)
{
    /// <summary>The <c>&lt;Type&gt;</c> the element is written inside, by its declared name.</summary>
    /// <remarks>
    /// By walking the parent chain rather than through <c>ParentElement</c>: an element's parent is
    /// the enclosing element's content list, not the element, so the property answers for a level
    /// further out than the one wanted here.
    /// </remarks>
    public string OwnerTypeName => Attribute(DbmlReferences.EnclosingElement(Element), "Name");

    /// <summary>The type an association points at, read off the element rather than the model.</summary>
    public string TargetTypeName => Attribute(Element, "Type");

    /// <remarks>
    /// The single-argument overload: the second parameter is the prefix the name must carry, and an
    /// empty string there matches nothing, because an unprefixed attribute reports its prefix as null.
    /// </remarks>
    private static string Attribute(XmlElementBaseSyntax? element, string name) =>
        XmlSpans.Decode(element?.GetAttributeValue(name));
}

/// <summary>
/// The names a <c>.dbml</c> writes inside its attribute values, which the model itself does not
/// record.
/// </summary>
/// <remarks>
/// <para>
/// A model is full of references, and every one of them is a string in an attribute:
/// <c>Type="Customer"</c> names a class the file declares further down, <c>ThisKey="CustomerId"</c>
/// names a column of the type it is written in, <c>OtherKey="Id"</c> names one of the type at the
/// other end, and <c>Type="System.Int32"</c> names something in C# entirely. None of them is a
/// declaration, so none is in <see cref="DbmlDatabase"/> — and without them F12 on any of the four
/// answers with the element the caret happens to be inside, which is the thing the reader is already
/// looking at.
/// </para>
/// <para>
/// Read from the syntax tree rather than the model, for the reason completion is: the element under
/// the caret is often half-typed and may not have made it into the model at all, and its
/// <c>Type=</c> is exactly what is needed to say what its <c>OtherKey</c> may name.
/// </para>
/// </remarks>
internal static class DbmlReferences
{
    /// <summary>
    /// The element an element is written inside.
    /// </summary>
    /// <remarks>
    /// Not <c>ParentElement</c>. A node's parent is the enclosing element's content list rather than
    /// the element itself, so that property skips a level and answers with the grandparent — which for
    /// a <c>&lt;Column&gt;</c> is the <c>&lt;Table&gt;</c> where the <c>&lt;Type&gt;</c> was wanted.
    /// </remarks>
    public static XmlElementBaseSyntax? EnclosingElement(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current is XmlElementBaseSyntax element)
                return element;
        }

        return null;
    }

    /// <summary>The reference the caret is inside, or nothing.</summary>
    public static DbmlReference? At(DbmlDocument document, int offset)
    {
        if (offset < 0 || offset > document.Text.Length)
            return null;

        int probe = offset == document.Text.Length && offset > 0 ? offset - 1 : offset;

        var node = SyntaxLocator.FindNode(
            document.Root, probe, descendIntoChildren: null, includeTrivia: true, excludeTerminal: false);

        XmlAttributeSyntax? attribute = null;
        XmlElementBaseSyntax? element = null;

        for (var current = node; current is not null; current = current.Parent)
        {
            attribute ??= current as XmlAttributeSyntax;
            element ??= current as XmlElementBaseSyntax;
        }

        if (attribute is null || element is null)
            return null;

        var valueSpan = attribute.ValueSpan();

        if (attribute.ValueNode is null || offset < valueSpan.Start || offset > valueSpan.End)
            return null;

        if (KindOf(element.Name ?? string.Empty, attribute.Name ?? string.Empty) is not { } kind)
            return null;

        return kind is DbmlReferenceKind.ThisKeyColumn or DbmlReferenceKind.OtherKeyColumn
            ? ColumnAt(document, valueSpan, offset, kind, element)
            : Whole(document, valueSpan, kind, element);
    }

    /// <summary>Every reference in the file, in document order.</summary>
    /// <remarks>
    /// For the classifier, which colours what it can resolve. One walk of the tree: the file is a
    /// model rather than a source file, so its whole attribute set is a few hundred nodes.
    /// </remarks>
    public static IEnumerable<DbmlReference> All(DbmlDocument document)
    {
        foreach (var element in document.Root.DescendantNodes().OfType<XmlElementBaseSyntax>())
        {
            string elementName = element.Name ?? string.Empty;

            foreach (var attribute in element.Attributes)
            {
                if (KindOf(elementName, attribute.Name ?? string.Empty) is not { } kind)
                    continue;

                var valueSpan = attribute.ValueSpan();

                if (attribute.ValueNode is null || valueSpan.IsEmpty)
                    continue;

                if (kind is DbmlReferenceKind.ThisKeyColumn or DbmlReferenceKind.OtherKeyColumn)
                {
                    foreach (var column in ColumnsIn(document, valueSpan, kind, element))
                        yield return column;

                    continue;
                }

                if (Whole(document, valueSpan, kind, element) is { } reference)
                    yield return reference;
            }
        }
    }

    /// <summary>
    /// Which of the four an attribute is, from the pair of names.
    /// </summary>
    /// <remarks>
    /// <c>Type</c> is two different things depending on the element it is on, and the difference is
    /// the whole reason this is a lookup rather than a name test: on a <c>&lt;Column&gt;</c> it is a
    /// CLR type and F12 belongs in C#, while on an <c>&lt;Association&gt;</c> it is an entity class
    /// this very file declares.
    /// </remarks>
    private static DbmlReferenceKind? KindOf(string element, string attribute) =>
        (element, attribute) switch
        {
            ("Association", "Type") => DbmlReferenceKind.ModelType,
            ("Association", "ThisKey") => DbmlReferenceKind.ThisKeyColumn,
            ("Association", "OtherKey") => DbmlReferenceKind.OtherKeyColumn,

            // Not ElementType: it names a type this file declares, and it carries that name in
            // IdRef or Name rather than in Type. Listing it here read as coverage it never had,
            // and now that an unresolved CLR type is an error, a wrong classification would be a
            // red squiggle on a name that is perfectly well declared two elements up.
            (_, "Type") when element is "Column" or "Parameter" or "Return"
                => DbmlReferenceKind.ClrType,

            _ => null,
        };

    private static DbmlReference? Whole(
        DbmlDocument document, TextSpan span, DbmlReferenceKind kind, XmlElementBaseSyntax element)
    {
        string name = document.Text.ToString(span).Trim();
        return name.Length == 0 ? null : new DbmlReference(kind, name, span, element);
    }

    private static DbmlReference? ColumnAt(
        DbmlDocument document, TextSpan span, int offset, DbmlReferenceKind kind,
        XmlElementBaseSyntax element)
    {
        foreach (var column in ColumnsIn(document, span, kind, element))
        {
            if (offset >= column.Span.Start && offset <= column.Span.End)
                return column;
        }

        return null;
    }

    /// <summary>
    /// The columns a key attribute lists, each with its own span.
    /// </summary>
    /// <remarks>
    /// Per column rather than per attribute, because a composite key is written
    /// <c>ThisKey="OrderId, LineNumber"</c> and F12 on the second name has to reach the second
    /// column. The spans are computed from the offsets within the value, so the surrounding spaces
    /// belong to neither.
    /// </remarks>
    private static IEnumerable<DbmlReference> ColumnsIn(
        DbmlDocument document, TextSpan span, DbmlReferenceKind kind, XmlElementBaseSyntax element)
    {
        string value = document.Text.ToString(span);
        int index = 0;

        while (index <= value.Length)
        {
            int comma = value.IndexOf(',', index);
            int end = comma < 0 ? value.Length : comma;

            int start = index;
            while (start < end && char.IsWhiteSpace(value[start]))
                start++;

            int stop = end;
            while (stop > start && char.IsWhiteSpace(value[stop - 1]))
                stop--;

            if (stop > start)
            {
                yield return new DbmlReference(
                    kind,
                    value[start..stop],
                    new TextSpan(span.Start + start, stop - start),
                    element);
            }

            if (comma < 0)
                yield break;

            index = comma + 1;
        }
    }
}

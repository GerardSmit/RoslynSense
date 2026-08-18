using System.Collections.Immutable;
using Microsoft.Language.Xml;
using TextSpan = Microsoft.CodeAnalysis.Text.TextSpan;

namespace RoslynMCP.Languages.Dbml.Core;

/// <summary>
/// Reads a LINQ to SQL model into a tree whose spans point at the exact characters in the buffer.
/// </summary>
/// <remarks>
/// <para>
/// A full-fidelity parse, for the reasons <c>ResxReader</c> sets out: every character of the source
/// is a node, so a span already <em>is</em> a range in the buffer, and the parse is error-tolerant,
/// so a file being typed into still yields its tables rather than stopping at the caret. The
/// <c>XDocument</c> the refresh command writes with is a separate read for a separate job — it
/// normalizes entities, which would move every span it produced.
/// </para>
/// <para>
/// Element names are matched without their namespace. A <c>.dbml</c> declares
/// <c>http://schemas.microsoft.com/linqtosql/dbml/2007</c> as its default namespace, so the parser
/// reports bare names; but a model that has been hand-edited to use a prefix is still a model, and
/// the parser already splits a name into its prefix and its local half.
/// </para>
/// </remarks>
internal static class DbmlReader
{
    public static DbmlDatabase Read(XmlDocumentSyntax root)
    {
        if (Find(root, "Database") is not { } database)
            return DbmlDatabase.Empty;

        var tables = ImmutableArray.CreateBuilder<DbmlTable>();
        var functions = ImmutableArray.CreateBuilder<DbmlFunction>();

        foreach (var child in database.Elements)
        {
            switch (LocalName(child))
            {
                case "Table" when ReadTable(child) is { } table:
                    tables.Add(table);
                    break;

                case "Function" when ReadFunction(child) is { } function:
                    functions.Add(function);
                    break;
            }
        }

        string name = Attribute(database, "Name") ?? string.Empty;

        return new DbmlDatabase(
            name,
            // SqlMetal's own default when the model does not name the context class.
            Attribute(database, "Class") is { Length: > 0 } @class ? @class : name,
            Attribute(database, "ContextNamespace"),
            Attribute(database, "EntityNamespace"),
            database.Span.ToRoslyn(),
            SelectionSpan(database),
            tables.ToImmutable(),
            functions.ToImmutable());
    }

    private static DbmlTable? ReadTable(XmlElementBaseSyntax element)
    {
        if (Attribute(element, "Name") is not { Length: > 0 } name)
            return null;

        var types = ImmutableArray.CreateBuilder<DbmlType>();

        foreach (var child in element.Elements)
        {
            if (LocalName(child) == "Type" && ReadType(child) is { } type)
                types.Add(type);
        }

        return new DbmlTable(
            name,
            // A table with no Member is exposed under its own name, dot and all; that is what
            // SqlMetal does, and a member name is not this reader's to invent.
            Attribute(element, "Member") is { Length: > 0 } member ? member : name,
            element.Span.ToRoslyn(),
            SelectionSpan(element),
            types.ToImmutable());
    }

    private static DbmlType? ReadType(XmlElementBaseSyntax element)
    {
        if (Attribute(element, "Name") is not { Length: > 0 } name)
            return null;

        var columns = ImmutableArray.CreateBuilder<DbmlColumn>();
        var associations = ImmutableArray.CreateBuilder<DbmlAssociation>();
        var derived = ImmutableArray.CreateBuilder<DbmlType>();

        foreach (var child in element.Elements)
        {
            switch (LocalName(child))
            {
                case "Column" when ReadColumn(child, name) is { } column:
                    columns.Add(column);
                    break;

                case "Association" when ReadAssociation(child, name) is { } association:
                    associations.Add(association);
                    break;

                // Inheritance: a derived type is nested inside the one it extends, and carries its
                // own columns. It is a class of its own, so it is a declaration of its own.
                case "Type" when ReadType(child) is { } nested:
                    derived.Add(nested);
                    break;
            }
        }

        return new DbmlType(
            name,
            element.Span.ToRoslyn(),
            SelectionSpan(element),
            columns.ToImmutable(),
            associations.ToImmutable(),
            derived.ToImmutable());
    }

    private static DbmlColumn? ReadColumn(XmlElementBaseSyntax element, string ownerTypeName)
    {
        if (Attribute(element, "Name") is not { Length: > 0 } name)
            return null;

        return new DbmlColumn(
            name,
            Attribute(element, "Member") is { Length: > 0 } member ? member : name,
            ownerTypeName,
            Attribute(element, "Type"),
            Attribute(element, "DbType"),
            Flag(element, "IsPrimaryKey"),
            Flag(element, "IsDbGenerated"),
            Flag(element, "IsVersion"),
            // The one flag whose absence does not mean false: LINQ to SQL infers nullability from
            // the CLR type when the model is silent, so a missing CanBeNull is unknown rather than
            // "not null". Read it as nullable, which is the reading that cannot fabricate a
            // constraint the database does not have.
            Flag(element, "CanBeNull", whenAbsent: true),
            element.Span.ToRoslyn(),
            SelectionSpan(element));
    }

    private static DbmlAssociation? ReadAssociation(XmlElementBaseSyntax element, string ownerTypeName)
    {
        if (Attribute(element, "Name") is not { Length: > 0 } name)
            return null;

        string member = Attribute(element, "Member") is { Length: > 0 } value ? value : name;

        return new DbmlAssociation(
            name,
            member,
            ownerTypeName,
            Attribute(element, "ThisKey") ?? string.Empty,
            Attribute(element, "OtherKey") ?? string.Empty,
            Attribute(element, "Type") ?? string.Empty,
            Flag(element, "IsForeignKey"),
            element.Span.ToRoslyn(),
            SelectionSpan(element));
    }

    private static DbmlFunction? ReadFunction(XmlElementBaseSyntax element)
    {
        if (Attribute(element, "Name") is not { Length: > 0 } name)
            return null;

        return new DbmlFunction(
            name,
            Attribute(element, "Method") is { Length: > 0 } method ? method : name,
            Flag(element, "IsComposable"),
            element.Span.ToRoslyn(),
            SelectionSpan(element));
    }

    // ---- Attribute access -----------------------------------------------------------------------

    /// <summary>
    /// Where a jump lands and a lens sits: the member name if the model states one, the database name
    /// otherwise, and the tag itself for an element carrying neither.
    /// </summary>
    /// <remarks>
    /// The member is preferred because it is the half a reader coming from C# recognises — F12 on
    /// <c>Product.Name</c> should land on <c>Member="Name"</c> and not on a <c>Name="ProductName"</c>
    /// beside it. The span is the value <em>as written</em>, so a name carrying an entity reference
    /// is longer than the decoded string and must not be rewritten in place; see
    /// <see cref="XmlSpans.ValueSpan"/>.
    /// </remarks>
    private static TextSpan SelectionSpan(XmlElementBaseSyntax element)
    {
        foreach (string name in (ReadOnlySpan<string>)["Member", "Class", "Name"])
        {
            if (AttributeNode(element, name) is { } attribute
                && attribute.ValueSpan() is { IsEmpty: false } span)
            {
                return span;
            }
        }

        return element.NameSpan();
    }

    private static XmlAttributeSyntax? AttributeNode(XmlElementBaseSyntax element, string name)
    {
        foreach (var attribute in element.Attributes)
        {
            if (string.Equals(attribute.NameNode.LocalName, name, StringComparison.Ordinal))
                return attribute;
        }

        return null;
    }

    private static string? Attribute(XmlElementBaseSyntax element, string name) =>
        AttributeNode(element, name) is { } attribute ? attribute.DecodedValue() : null;

    /// <summary>
    /// A boolean attribute, read the way the .NET XML serializer LINQ to SQL uses reads one — so
    /// <c>"true"</c> and <c>"1"</c> both count, and anything else is the default.
    /// </summary>
    private static bool Flag(XmlElementBaseSyntax element, string name, bool whenAbsent = false) =>
        Attribute(element, name) is { Length: > 0 } value
            ? value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1"
            : whenAbsent;

    private static XmlElementBaseSyntax? Find(XmlDocumentSyntax root, string name)
    {
        foreach (var element in root.DescendantNodes().OfType<XmlElementBaseSyntax>())
        {
            if (LocalName(element) == name)
                return element;
        }

        return null;
    }

    /// <summary>A name without its prefix — <c>l2s:Table</c> is a <c>Table</c>.</summary>
    private static string LocalName(XmlElementBaseSyntax element) =>
        element.NameNode?.LocalName ?? string.Empty;
}

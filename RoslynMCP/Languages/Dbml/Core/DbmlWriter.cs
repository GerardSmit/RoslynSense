using Microsoft.Language.Xml;

namespace RoslynMCP.Languages.Dbml.Core;

/// <summary>
/// Applies a <see cref="DbmlRefreshPlan"/> to the text of a <c>.dbml</c>.
/// </summary>
/// <remarks>
/// <para>
/// The same full-fidelity tree the rest of the pack reads with. Every character of the source is a
/// node, so a model that was edited in three places and written back out is the original file
/// everywhere else — the diff of a refresh is the columns that changed and nothing besides. That is
/// what this used to need <c>PreserveWhitespace</c>, <c>DisableFormatting</c>, hand-copied indent
/// text and a <c>TextWriter</c> subclass that lied about its encoding to achieve, and none of it is
/// needed when the writer never reformats in the first place.
/// </para>
/// <para>
/// Elements are written unprefixed, which is what a <c>.dbml</c> wants: the LINQ to SQL namespace is
/// bound as the default one on the root element, so an unprefixed child is already in it. An element
/// in no namespace is well-formed XML and is silently ignored by SqlMetal — a refresh that appeared
/// to work and generated nothing.
/// </para>
/// </remarks>
internal static class DbmlWriter
{
    /// <summary>
    /// The file with the plan applied, or <c>null</c> when it does not parse or does not contain the
    /// table the plan is for.
    /// </summary>
    /// <param name="includeRemovals">Whether to delete the columns the database no longer has. False
    /// is the answer when the user was asked and said to keep them, and it is also the safe default:
    /// a kept column is a property that still compiles.</param>
    /// <remarks>
    /// The row type is found again before every edit. Each one returns a new tree, so the element
    /// the last lookup produced belongs to the document as it was before — editing through it would
    /// build a model none of the earlier changes are in.
    /// </remarks>
    public static string? Apply(string xml, DbmlRefreshPlan plan, bool includeRemovals)
    {
        var document = Parser.ParseText(xml);

        // A root the parser synthesized an end tag for is a model the user is mid-edit in, and
        // overwriting it from the database would take their edit with it.
        if (document.RootSyntax is not XmlElementSyntax { EndTag.Span.Length: > 0 } original)
            return null;

        if (RowType(original, plan.TableName) is null)
            return null;

        XmlElementBaseSyntax root = original;

        foreach (var draft in plan.Added)
        {
            if (RowType(root, plan.TableName) is { } rowType)
                root = root.ReplaceNode(rowType, Insert(rowType, Column(draft), afterColumns: true));
        }

        foreach (var update in plan.Updated)
        {
            if (RowType(root, plan.TableName)?.Column(update.Existing.Name) is { } element)
                root = root.ReplaceNode(element, Updated(element, update.Refreshed));
        }

        if (includeRemovals)
        {
            foreach (var column in plan.Removed)
            {
                if (RowType(root, plan.TableName)?.Column(column.Name) is { } element)
                    root = root.RemoveNode(element, SyntaxRemoveOptions.KeepNoTrivia)!;
            }
        }

        foreach (var draft in plan.Associations)
        {
            if (TypeElement(root, draft.OwnerTypeName) is { } owner)
                root = root.ReplaceNode(owner, Insert(owner, Association(draft), afterColumns: false));
        }

        return document.ReplaceNode(original, root).ToFullString();
    }

    /// <summary>
    /// The file with new tables and functions written into it, or <c>null</c> when it does not parse.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Tables go after the last <c>&lt;Table&gt;</c> and functions after the last
    /// <c>&lt;Function&gt;</c>, falling back to each other's end, because that is the order SqlMetal
    /// writes and the order every hand-maintained model keeps — an added table appearing under the
    /// functions would read as misfiled even though SqlMetal would accept it.
    /// </para>
    /// <para>
    /// The nested content is composed as text using the document's own indent unit and line ending
    /// rather than node by node: the tree auto-indents an inserted element, but a subtree built by
    /// hand would need its trivia normalised against conventions this file already knows how to
    /// state, and text it states them in is text a test can assert on exactly.
    /// </para>
    /// </remarks>
    public static string? AddObjects(
        string xml,
        IReadOnlyList<DbmlTableDraft> tables,
        IReadOnlyList<DbmlFunctionDraft> functions)
    {
        var document = Parser.ParseText(xml);

        if (document.RootSyntax is not XmlElementSyntax { EndTag.Span.Length: > 0 } original)
            return null;

        string unit = original.GetIndentUnit();
        string newLine = original.GetNewLine();

        XmlElementBaseSyntax root = original;

        foreach (var draft in tables)
            root = InsertTopLevel(root, TableElement(draft, unit, newLine), asFunction: false);

        foreach (var draft in functions)
            root = InsertTopLevel(root, FunctionElement(draft, unit, newLine), asFunction: true);

        return document.ReplaceNode(original, root).ToFullString();
    }

    /// <summary>
    /// The root with one new top-level element among its own kind: a table after the tables, a
    /// function after the functions.
    /// </summary>
    private static XmlElementBaseSyntax InsertTopLevel(
        XmlElementBaseSyntax root, XmlElementBaseSyntax element, bool asFunction)
    {
        if (root is not XmlElementSyntax { Content: var content })
            return root;

        var lastTable = root.GetElementsByLocalName("Table").LastOrDefault();
        var lastFunction = root.GetElementsByLocalName("Function").LastOrDefault();

        int index;

        if (asFunction)
        {
            // After the last function; a model that has none gets its first at the end, which is
            // after the tables.
            index = lastFunction is null ? -1 : content.IndexOf(lastFunction) + 1;
        }
        else if (lastTable is not null)
        {
            index = content.IndexOf(lastTable) + 1;
        }
        else
        {
            // No tables yet: before the functions, or at the end of a model that has neither.
            var firstFunction = root.GetElementsByLocalName("Function").FirstOrDefault();
            index = firstFunction is null ? -1 : content.IndexOf(firstFunction);
        }

        return root.InsertChild(element, index);
    }

    private static XmlElementBaseSyntax TableElement(
        DbmlTableDraft draft, string unit, string newLine)
    {
        var text = new System.Text.StringBuilder();

        text.Append(StartTag(Empty("Table")
            .SetAttribute("Name", draft.Name)
            .SetAttribute("Member", draft.Member)));
        text.Append(newLine);

        text.Append(unit).Append(unit);
        text.Append(StartTag(Empty("Type").SetAttribute("Name", draft.TypeName)));
        text.Append(newLine);

        foreach (var column in draft.Columns)
            text.Append(unit).Append(unit).Append(unit)
                .Append(Column(column).ToFullString()).Append(newLine);

        text.Append(unit).Append(unit).Append("</Type>").Append(newLine);
        text.Append(unit).Append("</Table>");

        return Parse(text.ToString());
    }

    private static XmlElementBaseSyntax FunctionElement(
        DbmlFunctionDraft draft, string unit, string newLine)
    {
        var function = Empty("Function")
            .SetAttribute("Name", draft.Name)
            .SetAttribute("Method", draft.Method);

        if (draft.IsComposable)
            function = function.SetAttribute("IsComposable", "true");

        var text = new System.Text.StringBuilder();

        text.Append(StartTag(function)).Append(newLine);

        foreach (var parameter in draft.Parameters)
        {
            var element = Empty("Parameter")
                .SetAttribute("Name", parameter.Name)
                .SetAttribute("Type", parameter.ClrType)
                .SetAttribute("DbType", parameter.DbType);

            if (parameter.Direction is { } direction)
                element = element.SetAttribute("Direction", direction);

            text.Append(unit).Append(unit).Append(element.ToFullString()).Append(newLine);
        }

        if (draft.ElementTypeName is { } elementType)
        {
            text.Append(unit).Append(unit);
            text.Append(StartTag(Empty("ElementType").SetAttribute("Name", elementType)));
            text.Append(newLine);

            foreach (var column in draft.ElementColumns)
                text.Append(unit).Append(unit).Append(unit)
                    .Append(Column(column).ToFullString()).Append(newLine);

            text.Append(unit).Append(unit).Append("</ElementType>").Append(newLine);
        }
        else if (draft.ReturnClrType is { } returnType)
        {
            var element = Empty("Return").SetAttribute("Type", returnType);

            if (draft.ReturnDbType is { } dbType)
                element = element.SetAttribute("DbType", dbType);

            text.Append(unit).Append(unit).Append(element.ToFullString()).Append(newLine);
        }

        text.Append(unit).Append("</Function>");

        return Parse(text.ToString());
    }

    /// <summary>An attribute-only element reopened as a start tag, for composing nested text.</summary>
    private static string StartTag(XmlElementBaseSyntax empty) =>
        empty.ToFullString()[..^2].TrimEnd() + ">";

    private static XmlElementBaseSyntax Parse(string text) =>
        (XmlElementBaseSyntax)Parser.ParseText(text).RootSyntax!;

    /// <summary>The <c>&lt;Type&gt;</c> a table's rows are described by.</summary>
    private static XmlElementBaseSyntax? RowType(XmlElementBaseSyntax root, string tableName) =>
        root.GetElementsByLocalName("Table")
            .FirstOrDefault(table => string.Equals(
                table.GetAttributeValue("Name"), tableName, StringComparison.OrdinalIgnoreCase))
            ?.GetElementByLocalName("Type");

    /// <summary>
    /// A <c>&lt;Type&gt;</c> anywhere in the model, inherited ones included.
    /// </summary>
    /// <remarks>
    /// A descendant search rather than a walk of the tables: an association's owner is named, and a
    /// derived type is nested inside its base's element rather than under the table directly.
    /// </remarks>
    private static XmlElementBaseSyntax? TypeElement(XmlElementBaseSyntax root, string typeName) =>
        root.DescendantsByLocalName("Type").FirstOrDefault(type => string.Equals(
            type.GetAttributeValue("Name"), typeName, StringComparison.Ordinal));

    /// <summary>
    /// A column by the name it has in the database, or by the property it maps to when the two
    /// differ.
    /// </summary>
    private static XmlElementBaseSyntax? Column(this XmlElementBaseSyntax type, string columnName) =>
        type.GetElementsByLocalName("Column").FirstOrDefault(column => string.Equals(
            column.GetAttributeValue("Name") ?? column.GetAttributeValue("Member"),
            columnName,
            StringComparison.OrdinalIgnoreCase));

    private static XmlElementBaseSyntax Column(DbmlColumnDraft draft)
    {
        var element = Empty("Column")
            .SetAttribute("Name", draft.Name)
            .SetAttribute("Type", draft.ClrType)
            .SetAttribute("DbType", draft.DbType);

        // Only the attributes that are not the default, which is what SqlMetal writes and therefore
        // what a refresh has to write for the file not to churn on the next run.
        if (draft.IsPrimaryKey)
            element = element.SetAttribute("IsPrimaryKey", "true");
        if (draft.IsDbGenerated)
            element = element.SetAttribute("IsDbGenerated", "true");
        if (draft.IsVersion)
            element = element.SetAttribute("IsVersion", "true");
        if (!draft.CanBeNull)
            element = element.SetAttribute("CanBeNull", "false");

        return element;
    }

    /// <summary>
    /// The column's attributes brought in line with the database, leaving everything else alone.
    /// </summary>
    /// <remarks>
    /// <c>Member</c>, <c>Storage</c>, <c>AccessModifier</c> and <c>UpdateCheck</c> are never touched:
    /// they are the model's own decisions about the generated code and nothing in the database has an
    /// opinion about them. An attribute that has become the default is removed rather than set to
    /// <c>false</c>, so the element reads the way a freshly generated one does.
    /// </remarks>
    private static XmlElementBaseSyntax Updated(XmlElementBaseSyntax element, DbmlColumnDraft draft)
    {
        element = element
            .SetAttribute("Type", draft.ClrType)
            .SetAttribute("DbType", draft.DbType);

        element = Flag(element, "IsPrimaryKey", draft.IsPrimaryKey ? "true" : null);
        element = Flag(element, "IsDbGenerated", draft.IsDbGenerated ? "true" : null);
        element = Flag(element, "IsVersion", draft.IsVersion ? "true" : null);

        return Flag(element, "CanBeNull", draft.CanBeNull ? null : "false");
    }

    /// <summary>One attribute set to a value, or taken off when the value is <c>null</c>.</summary>
    private static XmlElementBaseSyntax Flag(XmlElementBaseSyntax element, string name, string? value)
    {
        if (value is not null)
            return element.SetAttribute(name, value);

        return element.GetAttribute(name) is { } attribute
            ? element.RemoveAttribute(attribute)
            : element;
    }

    private static XmlElementBaseSyntax Association(DbmlAssociationDraft draft)
    {
        var element = Empty("Association")
            .SetAttribute("Name", draft.Name)
            .SetAttribute("Member", draft.Member)
            .SetAttribute("ThisKey", draft.ThisKey)
            .SetAttribute("OtherKey", draft.OtherKey)
            .SetAttribute("Type", draft.TargetTypeName);

        // The child end says so; the parent end is a collection and says nothing, which is how LINQ
        // to SQL tells the two halves of one Name apart.
        return draft.IsForeignKey ? element.SetAttribute("IsForeignKey", "true") : element;
    }

    /// <summary>An attribute-only element, which is how a <c>.dbml</c> writes both of these.</summary>
    private static XmlElementBaseSyntax Empty(string name) =>
        (XmlElementBaseSyntax)Parser.ParseText($"<{name} />").RootSyntax!;

    /// <summary>
    /// The type with an element inserted among its own kind, indented like its neighbours.
    /// </summary>
    /// <remarks>
    /// Columns before associations, matching the order SqlMetal writes and every hand-edited model
    /// follows. The indentation is the library's to work out from what the document already does; a
    /// model indented with tabs, or with two spaces, or not at all is a model this must not reformat.
    /// </remarks>
    private static XmlElementBaseSyntax Insert(
        XmlElementBaseSyntax type, XmlElementBaseSyntax element, bool afterColumns)
    {
        var anchor = afterColumns
            ? type.GetElementsByLocalName("Column").LastOrDefault()
            : type.GetElementsByLocalName("Association").LastOrDefault()
                ?? type.GetElementsByLocalName("Column").LastOrDefault();

        // The index is into the content, which is nodes and not only elements, so it is counted
        // there rather than among the siblings of the anchor's kind.
        int index = anchor is null || type is not XmlElementSyntax { Content: var content }
            ? -1
            : content.IndexOf(anchor) + 1;

        return type.InsertChild(element, index);
    }
}

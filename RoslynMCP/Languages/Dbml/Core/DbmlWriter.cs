using System.Xml.Linq;

namespace RoslynMCP.Languages.Dbml.Core;

/// <summary>
/// Applies a <see cref="DbmlRefreshPlan"/> to the text of a <c>.dbml</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="XDocument"/> rather than the parser the rest of the pack reads with, and the split is
/// deliberate. Reading needs exact spans over text that is frequently half-typed, which is what
/// <c>Microsoft.Language.Xml</c> is for; writing needs to add elements and leave every byte it did not
/// touch alone, which is what <c>PreserveWhitespace</c> plus <c>DisableFormatting</c> does and what a
/// full-fidelity red-green tree would need a whole editing layer to do.
/// </para>
/// <para>
/// Every element is created in the LINQ to SQL namespace. An element in no namespace is well-formed
/// XML and is silently ignored by SqlMetal — a refresh that appeared to work and generated nothing.
/// </para>
/// </remarks>
internal static class DbmlWriter
{
    /// <summary>The namespace every element in a <c>.dbml</c> is in.</summary>
    public static readonly XNamespace Namespace =
        "http://schemas.microsoft.com/linqtosql/dbml/2007";

    /// <summary>
    /// The file with the plan applied, or <c>null</c> when it does not parse or does not contain the
    /// table the plan is for.
    /// </summary>
    /// <param name="includeRemovals">Whether to delete the columns the database no longer has. False
    /// is the answer when the user was asked and said to keep them, and it is also the safe default:
    /// a kept column is a property that still compiles.</param>
    public static string? Apply(string xml, DbmlRefreshPlan plan, bool includeRemovals)
    {
        XDocument document;

        try
        {
            document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        }
        catch (System.Xml.XmlException)
        {
            // A model that does not parse is one the user is mid-edit in, and overwriting it from the
            // database would take their edit with it.
            return null;
        }

        if (document.Root is not { } root)
            return null;

        if (TableElement(root, plan.TableName) is not { } table)
            return null;

        if (table.Elements(Namespace + "Type").FirstOrDefault() is not { } rowType)
            return null;

        foreach (var draft in plan.Added)
            Insert(rowType, ColumnElement(draft), afterColumns: true);

        foreach (var update in plan.Updated)
        {
            if (ColumnElement(rowType, update.Existing.Name) is { } element)
                Update(element, update.Refreshed);
        }

        if (includeRemovals)
        {
            foreach (var column in plan.Removed)
            {
                if (ColumnElement(rowType, column.Name) is { } element)
                    Remove(element);
            }
        }

        foreach (var draft in plan.Associations)
        {
            if (TypeElement(root, draft.OwnerTypeName) is { } owner)
                Insert(owner, AssociationElement(draft), afterColumns: false);
        }

        using var writer = new DeclaredEncodingWriter(document.Declaration?.Encoding);
        document.Save(writer, SaveOptions.DisableFormatting);
        return writer.ToString();
    }

    /// <summary>
    /// A <see cref="StringWriter"/> that reports the encoding the document declared rather than the
    /// one a string is held in.
    /// </summary>
    /// <remarks>
    /// <see cref="XDocument.Save(TextWriter, SaveOptions)"/> rewrites the XML declaration from the
    /// writer's <see cref="TextWriter.Encoding"/>, and a plain <c>StringWriter</c>'s is UTF-16 —
    /// because a .NET string is. So saving a file that says <c>encoding="utf-8"</c> through one
    /// silently turns the first line into <c>encoding="utf-16"</c>, which is then written to disk as
    /// UTF-8 and is a lie about the bytes underneath it. Reporting the declared encoding leaves the
    /// line exactly as the file had it.
    /// </remarks>
    private sealed class DeclaredEncodingWriter(string? declaredEncoding) : StringWriter
    {
        public override System.Text.Encoding Encoding { get; } = Resolve(declaredEncoding);

        /// <summary>
        /// UTF-8 for a name .NET does not know, and for a file with no declaration at all — which is
        /// what an XML document with none means.
        /// </summary>
        private static System.Text.Encoding Resolve(string? name)
        {
            if (name is not { Length: > 0 })
                return new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

            try
            {
                return System.Text.Encoding.GetEncoding(name);
            }
            catch (ArgumentException)
            {
                return new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            }
        }
    }

    private static XElement? TableElement(XElement root, string tableName) =>
        root.Elements(Namespace + "Table").FirstOrDefault(t =>
            string.Equals(t.Attribute("Name")?.Value, tableName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A <c>&lt;Type&gt;</c> anywhere in the model, inherited ones included.
    /// </summary>
    /// <remarks>
    /// A descendant search rather than a walk of the tables: an association's owner is named, and a
    /// derived type is nested inside its base's element rather than under the table directly.
    /// </remarks>
    private static XElement? TypeElement(XElement root, string typeName) =>
        root.Descendants(Namespace + "Type").FirstOrDefault(t =>
            string.Equals(t.Attribute("Name")?.Value, typeName, StringComparison.Ordinal));

    private static XElement? ColumnElement(XElement type, string columnName) =>
        type.Elements(Namespace + "Column").FirstOrDefault(c =>
            string.Equals(
                c.Attribute("Name")?.Value ?? c.Attribute("Member")?.Value,
                columnName,
                StringComparison.OrdinalIgnoreCase));

    private static XElement ColumnElement(DbmlColumnDraft draft)
    {
        var element = new XElement(Namespace + "Column",
            new XAttribute("Name", draft.Name),
            new XAttribute("Type", draft.ClrType),
            new XAttribute("DbType", draft.DbType));

        // Only the attributes that are not the default, which is what SqlMetal writes and therefore
        // what a refresh has to write for the file not to churn on the next run.
        if (draft.IsPrimaryKey)
            element.SetAttributeValue("IsPrimaryKey", "true");
        if (draft.IsDbGenerated)
            element.SetAttributeValue("IsDbGenerated", "true");
        if (draft.IsVersion)
            element.SetAttributeValue("IsVersion", "true");
        if (!draft.CanBeNull)
            element.SetAttributeValue("CanBeNull", "false");

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
    private static void Update(XElement element, DbmlColumnDraft draft)
    {
        element.SetAttributeValue("Type", draft.ClrType);
        element.SetAttributeValue("DbType", draft.DbType);
        element.SetAttributeValue("IsPrimaryKey", draft.IsPrimaryKey ? "true" : null);
        element.SetAttributeValue("IsDbGenerated", draft.IsDbGenerated ? "true" : null);
        element.SetAttributeValue("IsVersion", draft.IsVersion ? "true" : null);
        element.SetAttributeValue("CanBeNull", draft.CanBeNull ? null : "false");
    }

    private static XElement AssociationElement(DbmlAssociationDraft draft)
    {
        var element = new XElement(Namespace + "Association",
            new XAttribute("Name", draft.Name),
            new XAttribute("Member", draft.Member),
            new XAttribute("ThisKey", draft.ThisKey),
            new XAttribute("OtherKey", draft.OtherKey),
            new XAttribute("Type", draft.TargetTypeName));

        // The child end says so; the parent end is a collection and says nothing, which is how LINQ
        // to SQL tells the two halves of one Name apart.
        if (draft.IsForeignKey)
            element.SetAttributeValue("IsForeignKey", "true");

        return element;
    }

    /// <summary>
    /// Inserts an element among its own kind, indented like its neighbours.
    /// </summary>
    /// <remarks>
    /// Columns before associations, matching the order SqlMetal writes and every hand-edited model
    /// follows. The indentation is copied from the whitespace in front of an existing sibling rather
    /// than computed, because a model indented with tabs, or with two spaces, or not at all is a model
    /// this must not reformat — the diff of a refresh should be the columns that changed.
    /// </remarks>
    private static void Insert(XElement type, XElement element, bool afterColumns)
    {
        var columns = type.Elements(Namespace + "Column").ToList();
        var associations = type.Elements(Namespace + "Association").ToList();

        XElement? anchor = afterColumns
            ? columns.LastOrDefault()
            : associations.LastOrDefault() ?? columns.LastOrDefault();

        if (anchor is null)
        {
            type.Add(element);
            return;
        }

        // The element first and the whitespace second, because each insert goes immediately after the
        // anchor and so the later call ends up in front of the earlier one. The whitespace copied is
        // the anchor's own leading break and indent, which is what the new line needs too.
        anchor.AddAfterSelf(element);

        if (anchor.PreviousNode is XText indent)
            anchor.AddAfterSelf(new XText(indent.Value));
    }

    /// <summary>
    /// Removes an element and the whitespace that indented it, so a deletion does not leave a blank
    /// line where the column was.
    /// </summary>
    private static void Remove(XElement element)
    {
        var indent = element.PreviousNode as XText;
        element.Remove();
        indent?.Remove();
    }
}

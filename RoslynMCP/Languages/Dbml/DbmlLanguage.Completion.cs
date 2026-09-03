using Microsoft.Language.Xml;
using RoslynMCP.Languages.Dbml.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.Dbml;

internal sealed partial class DbmlLanguage : ILanguageCompletionProvider
{
    private const int KindProperty = 10;
    private const int KindValue = 12;
    private const int KindClass = 7;
    private const int KindField = 5;

    /// <summary>
    /// What can be typed where the caret is: an element the parent allows, an attribute the element
    /// allows, or a value the rest of the file already names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The vocabulary halves are a fixed list, which is worth having for the ordinary reason a schema
    /// is: <c>IsDbGenerated</c> and <c>AutoSync</c> are not names anyone remembers, and getting one
    /// subtly wrong produces a model that parses, generates, compiles, and is wrong at runtime.
    /// </para>
    /// <para>
    /// The value half is the part that could not come from a schema. <c>Type=</c> on an association
    /// must name a <c>&lt;Type&gt;</c> this file declares and <c>ThisKey</c>/<c>OtherKey</c> must name
    /// columns of the two types involved — all of which are in the buffer being edited, and none of
    /// which any static list could know.
    /// </para>
    /// <para>
    /// Items are self-contained: no <c>completionItem/resolve</c>, per the documentless-request
    /// contract — a resolve request carries no URI, so an item that needed the file to finish
    /// describing itself could not be finished.
    /// </para>
    /// </remarks>
    public async Task<CompletionList> CompletionAsync(
        CompletionParams p, LspResolveCache cache, CancellationToken ct)
    {
        string path = LspConverters.UriToPath(p.TextDocument.Uri);

        if (DbmlDocumentCache.Get(path) is not { } document)
            return new CompletionList(false, []);

        int offset = LspConverters.ToOffset(document.Text, p.Position);
        var context = DbmlCompletionResolver.Resolve(document, offset);

        var items = context.Site switch
        {
            DbmlSite.ElementName => Elements(context),
            DbmlSite.AttributeName => Attributes(context),
            DbmlSite.AttributeValue => Values(context, document.Database),
            _ => [],
        };

        if (items.Count == 0)
            return new CompletionList(false, []);

        var range = LspConverters.ToRange(document.Text.Lines, context.ReplaceSpan);

        return new CompletionList(false,
        [
            .. items.Select((item, index) => new CompletionItem(
                item.Label, item.Kind, item.Detail,
                SortText: index.ToString("D3"),
                FilterText: item.Label,
                TextEdit: new TextEdit(range, item.Label))),
        ]);
    }

    /// <summary>Nothing to resolve — every item is complete as sent.</summary>
    public Task<CompletionItem> ResolveCompletionAsync(
        CompletionItem item, LspResolveCache cache, CancellationToken ct) =>
        Task.FromResult(item);

    private readonly record struct Item(string Label, int Kind, string? Detail);

    /// <summary>
    /// The elements the parent allows, which is a short and completely fixed list.
    /// </summary>
    /// <remarks>
    /// A <c>&lt;Type&gt;</c> is under both a <c>&lt;Table&gt;</c> and another <c>&lt;Type&gt;</c>: the
    /// first is a table's row type and the second is a subclass of it, which is how LINQ to SQL spells
    /// single-table inheritance.
    /// </remarks>
    private static IReadOnlyList<Item> Elements(in DbmlCompletionContext context) =>
        context.ParentName switch
        {
            "Database" =>
            [
                new("Table", KindClass, "A database table and the context property for it"),
                new("Function", KindClass, "A stored procedure or user-defined function"),
                new("Connection", KindClass, "The design-time connection"),
            ],

            "Table" => [new Item("Type", KindClass, "The entity class for the table's rows")],

            "Type" =>
            [
                new("Column", KindProperty, "A database column and the property for it"),
                new("Association", KindProperty, "One end of a foreign-key relationship"),
                new("Type", KindClass, "A subclass, for single-table inheritance"),
            ],

            "Function" =>
            [
                new("Parameter", KindProperty, "A parameter of the procedure"),
                new("ElementType", KindClass, "The shape of a returned row"),
                new("Return", KindClass, "The scalar the function returns"),
            ],

            _ => [],
        };

    private static IReadOnlyList<Item> Attributes(in DbmlCompletionContext context) =>
        context.ElementName switch
        {
            "Database" =>
            [
                new("Name", KindProperty, "The database's name"),
                new("Class", KindProperty, "The generated DataContext class"),
                new("EntityNamespace", KindProperty, "Namespace for the entity classes"),
                new("ContextNamespace", KindProperty, "Namespace for the DataContext"),
                new("Serialization", KindProperty, "None | Unidirectional"),
            ],

            "Table" =>
            [
                new("Name", KindProperty, "The table, schema-qualified — dbo.Orders"),
                new("Member", KindProperty, "The Table<T> property on the context"),
            ],

            "Type" =>
            [
                new("Name", KindProperty, "The generated entity class"),
                new("InheritanceCode", KindProperty, "This subclass's discriminator value"),
                new("IsInheritanceDefault", KindProperty, "The subclass for an unmatched discriminator"),
            ],

            "Column" =>
            [
                new("Name", KindProperty, "The database column; defaults to Member"),
                new("Member", KindProperty, "The generated property; defaults to Name"),
                new("Type", KindProperty, "The CLR type — System.Int32"),
                new("DbType", KindProperty, "The column as the database has it"),
                new("IsPrimaryKey", KindProperty, "Part of the primary key"),
                new("IsDbGenerated", KindProperty, "The database fills it in"),
                new("IsVersion", KindProperty, "The row-version column, for concurrency"),
                new("CanBeNull", KindProperty, "Whether the column is nullable"),
                new("IsDiscriminator", KindProperty, "Selects the subclass, for inheritance"),
                new("Storage", KindProperty, "The backing field"),
                new("AutoSync", KindProperty, "Never | OnInsert | OnUpdate | Always"),
                new("UpdateCheck", KindProperty, "Always | Never | WhenChanged"),
                new("Expression", KindProperty, "The computed column's SQL"),
                new("AccessModifier", KindProperty, "The property's accessibility"),
            ],

            "Association" =>
            [
                new("Name", KindProperty, "The constraint — shared by both ends"),
                new("Member", KindProperty, "The generated property"),
                new("ThisKey", KindProperty, "This type's columns in the key"),
                new("OtherKey", KindProperty, "The target type's columns in the key"),
                new("Type", KindProperty, "The entity class at the other end"),
                new("IsForeignKey", KindProperty, "This end holds the key"),
                new("Cardinality", KindProperty, "One | Many"),
                new("DeleteRule", KindProperty, "The ON DELETE behaviour"),
                new("Storage", KindProperty, "The backing field"),
            ],

            "Function" =>
            [
                new("Name", KindProperty, "The procedure, schema-qualified"),
                new("Method", KindProperty, "The generated method on the context"),
                new("IsComposable", KindProperty, "A function that can appear in a query"),
                new("HasMultipleResults", KindProperty, "Returns more than one shape"),
            ],

            _ => [],
        };

    /// <summary>
    /// The values the file itself already names, plus the fixed vocabularies.
    /// </summary>
    /// <remarks>
    /// A key list is offered from the type at the correct end of the relationship: <c>ThisKey</c> from
    /// the type the element is written in and <c>OtherKey</c> from the one <c>Type=</c> names. Getting
    /// those two the wrong way round produces a model that generates and then fails at runtime with a
    /// message about a mapping, which is the mistake this exists to prevent.
    /// </remarks>
    private static IReadOnlyList<Item> Values(in DbmlCompletionContext context, DbmlDatabase database)
    {
        if (context.AttributeName is not { } attribute)
            return [];

        if (attribute is "IsPrimaryKey" or "IsDbGenerated" or "IsVersion" or "CanBeNull"
            or "IsForeignKey" or "IsDiscriminator" or "IsComposable" or "HasMultipleResults"
            or "IsInheritanceDefault")
        {
            return [new Item("true", KindValue, null), new Item("false", KindValue, null)];
        }

        switch (context.ElementName, attribute)
        {
            case ("Association", "Type"):
                return [.. database.AllTypes().Select(t => new Item(t.Name, KindClass, null))];

            case ("Association", "ThisKey"):
                return Columns(database, OwnerOf(context));

            case ("Association", "OtherKey"):
                return Columns(database, TargetOf(context));

            case ("Association", "Cardinality"):
                return [new Item("One", KindValue, null), new Item("Many", KindValue, null)];

            case ("Column", "AutoSync"):
                return
                [
                    new Item("Never", KindValue, null), new Item("OnInsert", KindValue, null),
                    new Item("OnUpdate", KindValue, null), new Item("Always", KindValue, null),
                    new Item("Default", KindValue, null),
                ];

            case ("Column", "UpdateCheck"):
                return
                [
                    new Item("Always", KindValue, null), new Item("Never", KindValue, null),
                    new Item("WhenChanged", KindValue, null),
                ];

            case ("Database", "Serialization"):
                return [new Item("None", KindValue, null), new Item("Unidirectional", KindValue, null)];

            default:
                return [];
        }
    }

    /// <summary>The type the association points at, read off the element being typed.</summary>
    /// <remarks>
    /// Off the live node rather than the parsed model, because the element the caret is in is
    /// half-typed and may not be in the model at all yet — which is precisely when its
    /// <c>Type=</c> is needed to say what its <c>OtherKey</c> may be.
    /// </remarks>
    private static string TargetOf(in DbmlCompletionContext context) =>
        Attribute(context.Element, "Type");

    /// <summary>The <c>&lt;Type&gt;</c> the element is written inside, by its declared name.</summary>
    /// <remarks>
    /// Through <see cref="DbmlReferences.EnclosingElement"/> rather than <c>ParentElement</c>, which
    /// skips a level — see the remarks there.
    /// </remarks>
    private static string OwnerOf(in DbmlCompletionContext context) =>
        context.Element is { } element
            ? Attribute(DbmlReferences.EnclosingElement(element), "Name")
            : string.Empty;

    /// <remarks>
    /// No prefix, which is what a <c>.dbml</c> attribute has: the second argument is the prefix the
    /// name must carry, and an empty string there matches nothing, because an unprefixed attribute
    /// reports its prefix as null.
    /// </remarks>
    private static string Attribute(XmlElementBaseSyntax? element, string name) =>
        element?.GetAttributeValue(name) ?? string.Empty;

    private static IReadOnlyList<Item> Columns(DbmlDatabase database, string typeName)
    {
        if (typeName.Length == 0)
            return [];

        var type = database.AllTypes().FirstOrDefault(t =>
            string.Equals(t.Name, typeName, StringComparison.Ordinal));

        return type is null
            ? []
            : [.. type.Columns.Select(c => new Item(c.Name, KindField, c.DbType))];
    }
}

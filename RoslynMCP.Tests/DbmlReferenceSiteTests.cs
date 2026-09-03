using Microsoft.CodeAnalysis.Text;
using Microsoft.Language.Xml;
using RoslynMCP.Languages.Dbml.Core;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The names a <c>.dbml</c> writes inside its attribute values, and which of them the caret is in.
/// </summary>
/// <remarks>
/// These are what F12 and the classifier both start from, so the cases that matter are the ones where
/// the same attribute name means two different things — <c>Type</c> on a column is a CLR type and
/// <c>Type</c> on an association is a class this file declares — and the one where an attribute holds
/// more than one name.
/// </remarks>
public class DbmlReferenceSiteTests
{
    private const string Ns = "http://schemas.microsoft.com/linqtosql/dbml/2007";

    private const string Model = $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Database Name="Shop" Class="ShopDataContext" xmlns="{Ns}">
          <Table Name="dbo.Lines" Member="Lines">
            <Type Name="Line">
              <Column Name="OrderId" Type="System.Int32" DbType="Int NOT NULL" CanBeNull="false" />
              <Column Name="LineNumber" Type="System.Int32" DbType="Int NOT NULL" CanBeNull="false" />
              <Column Name="Kind" Type="Shop.Data.LineKind" DbType="Int NOT NULL" CanBeNull="false" />
              <Association Name="Lines_Orders" Member="Order" ThisKey="OrderId, LineNumber" OtherKey="Id, Number" Type="Order" IsForeignKey="true" />
            </Type>
          </Table>
          <Table Name="dbo.Orders" Member="Orders">
            <Type Name="Order">
              <Column Name="Id" Type="System.Int32" DbType="Int NOT NULL" IsPrimaryKey="true" CanBeNull="false" />
              <Column Name="Number" Type="System.Int32" DbType="Int NOT NULL" CanBeNull="false" />
            </Type>
          </Table>
        </Database>
        """;

    private static DbmlDocument Document()
    {
        var text = SourceText.From(Model);
        var root = Parser.ParseText(Model);
        return new DbmlDocument("Shop.dbml", text, DbmlReader.Read(root), root);
    }

    /// <summary>The offset of the first character of <paramref name="value"/> in the model.</summary>
    private static int Caret(string value) => Model.IndexOf(value, StringComparison.Ordinal);

    [Fact]
    public void TypeOnAnAssociationIsAClassTheFileDeclares()
    {
        var reference = DbmlReferences.At(Document(), Caret("\"Order\" IsForeignKey") + 1);

        Assert.NotNull(reference);
        Assert.Equal(DbmlReferenceKind.ModelType, reference!.Value.Kind);
        Assert.Equal("Order", reference.Value.Name);
    }

    [Fact]
    public void TypeOnAColumnIsAClrTypeInstead()
    {
        // The same attribute name, and the difference is the element it sits on — which is why the
        // kind is a lookup on the pair rather than a test of the name.
        var reference = DbmlReferences.At(Document(), Caret("Shop.Data.LineKind"));

        Assert.NotNull(reference);
        Assert.Equal(DbmlReferenceKind.ClrType, reference!.Value.Kind);
        Assert.Equal("Shop.Data.LineKind", reference.Value.Name);
    }

    [Fact]
    public void EachColumnInACompositeKeyIsItsOwnReference()
    {
        var document = Document();

        var first = DbmlReferences.At(document, Caret("OrderId, LineNumber"));
        var second = DbmlReferences.At(document, Caret("OrderId, LineNumber") + "OrderId, ".Length);

        Assert.Equal("OrderId", first!.Value.Name);
        Assert.Equal("LineNumber", second!.Value.Name);

        // The span is the name's own characters, so a jump lands on the word rather than the list.
        Assert.Equal("LineNumber", Model.Substring(second.Value.Span.Start, second.Value.Span.Length));
    }

    [Fact]
    public void AKeyKnowsWhichTypeItsColumnsBelongTo()
    {
        var document = Document();

        var thisKey = DbmlReferences.At(document, Caret("OrderId, LineNumber"))!.Value;
        var otherKey = DbmlReferences.At(document, Caret("Id, Number"))!.Value;

        Assert.Equal(DbmlReferenceKind.ThisKeyColumn, thisKey.Kind);
        Assert.Equal("Line", thisKey.OwnerTypeName);

        Assert.Equal(DbmlReferenceKind.OtherKeyColumn, otherKey.Kind);
        Assert.Equal("Order", otherKey.TargetTypeName);
    }

    [Fact]
    public void AnAttributeThatNamesNothingIsNotAReference()
    {
        // Member and Name are the model's own words for things, not references to them.
        Assert.Null(DbmlReferences.At(Document(), Caret("\"Lines_Orders\"") + 1));
        Assert.Null(DbmlReferences.At(Document(), Caret("Int NOT NULL")));
    }

    [Fact]
    public void EveryReferenceInTheFileIsFound()
    {
        var all = DbmlReferences.All(Document()).ToList();

        // Five column types, one association target, and the four names in the two key lists.
        Assert.Equal(5, all.Count(r => r.Kind == DbmlReferenceKind.ClrType));
        Assert.Single(all, r => r.Kind == DbmlReferenceKind.ModelType);
        Assert.Equal(2, all.Count(r => r.Kind == DbmlReferenceKind.ThisKeyColumn));
        Assert.Equal(2, all.Count(r => r.Kind == DbmlReferenceKind.OtherKeyColumn));

        // Every span is the text it says it is, which is what the classifier colours.
        Assert.All(all, r =>
            Assert.Equal(r.Name, Model.Substring(r.Span.Start, r.Span.Length)));
    }
}

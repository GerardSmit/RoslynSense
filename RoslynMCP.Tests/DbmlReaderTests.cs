using Microsoft.Language.Xml;
using TextSpan = Microsoft.CodeAnalysis.Text.TextSpan;
using RoslynMCP.Languages.Dbml.Core;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The model every <c>.dbml</c> feature is built on: the spans a lens sits over and a jump lands on,
/// and the two defaults SqlMetal applies silently and the reader therefore has to apply too.
/// </summary>
public class DbmlReaderTests
{
    private const string Ns = "http://schemas.microsoft.com/linqtosql/dbml/2007";

    private static (DbmlDatabase Database, string Text) Read(string body)
    {
        string text = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Database Name="Shop" Class="ShopDataContext" xmlns="{Ns}">
            {body}
            </Database>
            """.ReplaceLineEndings("\n");

        return (DbmlReader.Read(Parser.ParseText(text)), text);
    }

    private static string At(string text, TextSpan span) => text.Substring(span.Start, span.Length);

    [Fact]
    public void TheSelectionSpanIsTheMemberNameWithoutItsQuotes()
    {
        var (database, text) = Read("""
              <Table Name="dbo.Products" Member="Products">
                <Type Name="Product">
                  <Column Name="ProductId" Member="Id" Type="System.Int32" DbType="Int NOT NULL" />
                </Type>
              </Table>
            """);

        var table = Assert.Single(database.Tables);
        var column = Assert.Single(table.RowType!.Columns);

        // The exact characters a jump lands on and a lens sits above. Quotes excluded, or the
        // highlight covers them.
        Assert.Equal("Products", At(text, table.SelectionSpan));
        Assert.Equal("Id", At(text, column.SelectionSpan));

        // The whole element is the outline's range, so it has to start at the tag.
        Assert.StartsWith("<Column", At(text, column.Span));
    }

    [Fact]
    public void AColumnWithoutAMemberIsNamedAfterItsColumn()
    {
        // SqlMetal omits Member when it would repeat Name, and generates the property from Name.
        // Reading Member as empty would leave the property unbindable.
        var (database, _) = Read("""
              <Table Name="dbo.Products" Member="Products">
                <Type Name="Product">
                  <Column Name="Name" Type="System.String" />
                </Type>
              </Table>
            """);

        var column = Assert.Single(database.Tables[0].RowType!.Columns);

        Assert.Equal("Name", column.Name);
        Assert.Equal("Name", column.Member);
    }

    [Fact]
    public void AColumnIsNullableWhenTheModelSaysNothing()
    {
        // LINQ to SQL infers nullability when CanBeNull is absent, and reading it as nullable cannot
        // fabricate a constraint the database does not have — reading it as NOT NULL could.
        var (database, _) = Read("""
              <Table Name="dbo.Products" Member="Products">
                <Type Name="Product">
                  <Column Name="Name" Type="System.String" />
                  <Column Name="Id" Type="System.Int32" CanBeNull="false" />
                </Type>
              </Table>
            """);

        var columns = database.Tables[0].RowType!.Columns;

        Assert.True(columns[0].CanBeNull);
        Assert.False(columns[1].CanBeNull);
    }

    [Fact]
    public void BothEndsOfARelationshipShareANameAndAreToldApartByMember()
    {
        var (database, _) = Read("""
              <Table Name="dbo.Orders" Member="Orders">
                <Type Name="Order">
                  <Column Name="CustomerId" Type="System.Int32" />
                  <Association Name="FK_Orders_Customers" Member="Customer" ThisKey="CustomerId"
                               OtherKey="Id" Type="Customer" IsForeignKey="true" />
                </Type>
              </Table>
              <Table Name="dbo.Customers" Member="Customers">
                <Type Name="Customer">
                  <Column Name="Id" Type="System.Int32" />
                  <Association Name="FK_Orders_Customers" Member="Orders" ThisKey="Id"
                               OtherKey="CustomerId" Type="Order" />
                </Type>
              </Table>
            """);

        var child = database.AllTypes().Single(t => t.Name == "Order").Associations[0];
        var parent = database.AllTypes().Single(t => t.Name == "Customer").Associations[0];

        Assert.Equal(child.Name, parent.Name);
        Assert.NotEqual(child.Key, parent.Key);
        Assert.True(child.IsForeignKey);
        Assert.False(parent.IsForeignKey);
    }

    [Fact]
    public void ADerivedTypeIsReachedByTheSameWalkAsItsBase()
    {
        // Inheritance nests <Type> inside <Type>, so a walk that only looked one level down would
        // silently drop every column of every subclass.
        var (database, _) = Read("""
              <Table Name="dbo.People" Member="People">
                <Type Name="Person">
                  <Column Name="Id" Type="System.Int32" />
                  <Type Name="Employee">
                    <Column Name="Salary" Type="System.Decimal" />
                  </Type>
                </Type>
              </Table>
            """);

        Assert.Equal(["Person", "Employee"], database.AllTypes().Select(t => t.Name));
        Assert.Contains(database.AllDeclarations(), d => d.Key == "column:Employee.Salary");
    }

    [Fact]
    public void EveryDeclarationIsFoundByItsOwnKey()
    {
        // The contract the whole pack rests on: the index hands back a key read from the file on
        // disk, and the caller resolves it against the buffer the editor is showing.
        var (database, _) = Read("""
              <Table Name="dbo.Products" Member="Products">
                <Type Name="Product">
                  <Column Name="Id" Type="System.Int32" />
                </Type>
              </Table>
              <Function Name="dbo.usp_Restock" Method="Restock" />
            """);

        foreach (var declaration in database.AllDeclarations())
            Assert.Same(declaration, database.Find(declaration.Key));
    }

    [Fact]
    public void AFileThatStopsBeingXmlHalfwayStillYieldsWhatCameBefore()
    {
        // The reader runs on every keystroke, and a model mid-edit is the normal case rather than
        // the exceptional one.
        var (database, _) = Read("""
              <Table Name="dbo.Products" Member="Products">
                <Type Name="Product">
                  <Column Name="Id" Type="System.Int32" />
                  <Column Name="
            """);

        Assert.Equal("Product", database.Tables[0].RowType!.Name);
        Assert.Contains(database.Tables[0].RowType!.Columns, c => c.Name == "Id");
    }
}

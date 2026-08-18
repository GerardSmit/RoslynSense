using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Dbml.Core;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The join between a <c>.dbml</c> declaration and the C# SqlMetal generated from it, which is what
/// every other feature in the pack is built on.
/// </summary>
/// <remarks>
/// <para>
/// The designer here is written by hand rather than by SqlMetal, so that the two cases the real tool
/// produces silently are both present: a <c>[Column]</c> whose <c>Name</c> was omitted because it
/// equalled the member, and an <c>[Association]</c> whose <c>Name</c> is identical on both ends.
/// </para>
/// <para>
/// The LINQ to SQL attributes are declared in the fixture itself. The binder matches them on their
/// simple name — generated code alias-qualifies everything, so the namespace is the unstable half —
/// which means the test needs no reference to <c>System.Data.Linq</c>, an assembly that does not exist
/// on the runtime these tests run on.
/// </para>
/// </remarks>
public class DbmlBindingTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "roslynsense-dbml-" + Guid.NewGuid().ToString("N"));

    private const string Model = """
        <?xml version="1.0" encoding="utf-8"?>
        <Database Name="Shop" Class="ShopDataContext" xmlns="http://schemas.microsoft.com/linqtosql/dbml/2007">
          <Table Name="dbo.Orders" Member="Orders">
            <Type Name="Order">
              <Column Name="Id" Type="System.Int32" DbType="Int NOT NULL IDENTITY" IsPrimaryKey="true" />
              <Column Name="Reference" Type="System.String" DbType="NVarChar(20)" />
              <Column Name="CustomerId" Type="System.Int32" DbType="Int NOT NULL" />
              <Association Name="FK_Orders_Customers" Member="Customer" ThisKey="CustomerId" OtherKey="Id" Type="Customer" IsForeignKey="true" />
            </Type>
          </Table>
          <Table Name="dbo.Customers" Member="Customers">
            <Type Name="Customer">
              <Column Name="Id" Type="System.Int32" DbType="Int NOT NULL" IsPrimaryKey="true" />
              <Association Name="FK_Orders_Customers" Member="Orders" ThisKey="Id" OtherKey="CustomerId" Type="Order" />
            </Type>
          </Table>
          <Function Name="dbo.usp_Restock" Method="Restock" />
        </Database>
        """;

    private const string Designer = """
        namespace System.Data.Linq
        {
            public class DataContext { }
            public class Table<T> { }
            public class ISingleResult<T> { }
        }

        namespace System.Data.Linq.Mapping
        {
            public sealed class DatabaseAttribute : System.Attribute { public string Name { get; set; } }
            public sealed class TableAttribute : System.Attribute { public string Name { get; set; } }
            public sealed class ColumnAttribute : System.Attribute { public string Name { get; set; } public string Storage { get; set; } }
            public sealed class AssociationAttribute : System.Attribute { public string Name { get; set; } public string ThisKey { get; set; } public string OtherKey { get; set; } }
            public sealed class FunctionAttribute : System.Attribute { public string Name { get; set; } }
        }

        namespace Shop
        {
            using System.Data.Linq;
            using System.Data.Linq.Mapping;

            [Database(Name = "Shop")]
            public partial class ShopDataContext : DataContext
            {
                public Table<Order> Orders { get { return null; } }
                public Table<Customer> Customers { get { return null; } }

                [Function(Name = "dbo.usp_Restock")]
                public int Restock() { return 0; }
            }

            [Table(Name = "dbo.Orders")]
            public partial class Order
            {
                [Column(Name = "Id", Storage = "_Id")]
                public int Id { get; set; }

                // No Name: SqlMetal omits it when the column is named after the member.
                [Column(Storage = "_Reference")]
                public string Reference { get; set; }

                [Column(Name = "CustomerId", Storage = "_CustomerId")]
                public int CustomerId { get; set; }

                [Association(Name = "FK_Orders_Customers", ThisKey = "CustomerId", OtherKey = "Id")]
                public Customer Customer { get; set; }
            }

            [Table(Name = "dbo.Customers")]
            public partial class Customer
            {
                [Column(Name = "Id", Storage = "_Id")]
                public int Id { get; set; }

                [Association(Name = "FK_Orders_Customers", ThisKey = "Id", OtherKey = "CustomerId")]
                public System.Collections.Generic.List<Order> Orders { get; set; }
            }
        }
        """;

    private string ModelPath => Path.Combine(_directory, "Shop.dbml");

    private string DesignerPath => Path.Combine(_directory, "Shop.designer.cs");

    public DbmlBindingTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(ModelPath, Model);
        File.WriteAllText(DesignerPath, Designer);

        DbmlDocumentCache.Clear();
        DbmlGeneratedIndex.Clear();
        DbmlSourceMappingService.Clear();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private Project CreateProject(AdhocWorkspace workspace, bool includeDesigner = true)
    {
        var projectId = ProjectId.CreateNewId();

        var info = ProjectInfo.Create(
            projectId, VersionStamp.Create(), "Shop", "Shop", LanguageNames.CSharp,
            filePath: Path.Combine(_directory, "Shop.csproj"),
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences:
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location),
            ]);

        var solution = workspace.CurrentSolution.AddProject(info);

        if (includeDesigner)
        {
            solution = solution.AddDocument(
                DocumentId.CreateNewId(projectId), "Shop.designer.cs",
                SourceText.From(Designer), filePath: DesignerPath);
        }

        return solution.GetProject(projectId)!;
    }

    private async Task<DbmlGeneratedIndex> IndexAsync(bool includeDesigner = true)
    {
        using var workspace = new AdhocWorkspace();
        return await DbmlGeneratedIndex.GetAsync(
            ModelPath, CreateProject(workspace, includeDesigner), default);
    }

    [Fact]
    public async Task AColumnBindsWhetherOrNotItsAttributeRepeatsTheName()
    {
        var index = await IndexAsync();

        // Both spellings SqlMetal produces. Reading the one without a Name as unbound would leave
        // every column whose name matches its property with no reference count at all.
        Assert.Equal("Id", index.SymbolFor("column:Order.Id")?.Name);
        Assert.Equal("Reference", index.SymbolFor("column:Order.Reference")?.Name);
    }

    [Fact]
    public async Task TheTwoEndsOfARelationshipBindToDifferentProperties()
    {
        var index = await IndexAsync();

        var child = index.SymbolFor("association:Order.Customer");
        var parent = index.SymbolFor("association:Customer.Orders");

        Assert.NotNull(child);
        Assert.NotNull(parent);
        Assert.NotSame(child, parent);
        Assert.Equal("Customer", child!.Name);
        Assert.Equal("Orders", parent!.Name);
    }

    [Fact]
    public async Task TheTableTheTypeAndTheFunctionAllBind()
    {
        var index = await IndexAsync();

        Assert.Equal("Orders", index.SymbolFor("table:dbo.Orders")?.Name);
        Assert.Equal("Order", index.SymbolFor("type:Order")?.Name);
        Assert.Equal("Restock", index.SymbolFor("function:dbo.usp_Restock")?.Name);
        Assert.Equal("ShopDataContext", index.SymbolFor("database")?.Name);
    }

    [Fact]
    public async Task TheWayBackFromASymbolIsTheDeclarationItCameFrom()
    {
        // This is what F12 in C# rides on: the caret is on a generated property and the answer is
        // the model element it was written from.
        var index = await IndexAsync();
        var symbol = index.SymbolFor("column:Order.CustomerId")!;

        var reference = index.DeclarationFor(symbol);

        Assert.NotNull(reference);
        Assert.Equal("column:Order.CustomerId", reference!.Value.Key);
        Assert.Equal(DbmlDeclarationKind.Column, reference.Value.Kind);
    }

    [Fact]
    public async Task ADesignerTheProjectDoesNotCompileBindsNothingAndClaimsNothing()
    {
        // A model whose project has never been built is not broken, it is unbuilt — and nothing may
        // be withdrawn from F12 on the strength of a file path alone.
        var index = await IndexAsync(includeDesigner: false);

        Assert.True(index.IsEmpty);
        Assert.False(DbmlSourceMappingService.IsBoundDesignerPath(DesignerPath));
    }

    [Fact]
    public async Task ADesignerThatBoundIsTheOneF12WithdrawsFrom()
    {
        await IndexAsync();

        Assert.True(DbmlSourceMappingService.IsBoundDesignerPath(DesignerPath));

        // A designer beside a .settings or a .resx derives the same shape of path and is never in
        // the record, because the record is written by the binder rather than by the path rule.
        Assert.False(DbmlSourceMappingService.IsBoundDesignerPath(
            Path.Combine(_directory, "Settings.designer.cs")));
    }

    [Fact]
    public async Task TheDesignerIsRecognisedAsGeneratedSoItsOwnMentionsCanBeExcluded()
    {
        var index = await IndexAsync();

        Assert.True(index.IsGenerated(DesignerPath));
        Assert.False(index.IsGenerated(Path.Combine(_directory, "Program.cs")));
    }
}

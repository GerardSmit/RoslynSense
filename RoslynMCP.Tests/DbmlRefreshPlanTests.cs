using System.Collections.Immutable;
using Microsoft.Language.Xml;
using RoslynMCP.Languages.Dbml.Core;
using RoslynMCP.Services.Database;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The diff between a model and a live table, and the write that follows from it.
/// </summary>
/// <remarks>
/// Pure throughout — the planner takes a parsed table and a described schema, and the writer takes
/// text. No database is involved, which is the point: the interesting cases here are the ones a
/// developer would have to arrange a server to reach.
/// </remarks>
public class DbmlRefreshPlanTests
{
    private const string Ns = "http://schemas.microsoft.com/linqtosql/dbml/2007";

    private const string TwoTables = $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Database Name="Shop" Class="ShopDataContext" xmlns="{Ns}">
          <Table Name="dbo.Orders" Member="Orders">
            <Type Name="Order">
              <Column Name="Id" Type="System.Int32" DbType="Int NOT NULL IDENTITY" IsPrimaryKey="true" IsDbGenerated="true" CanBeNull="false" />
              <Column Name="CustomerId" Type="System.Int32" DbType="Int NOT NULL" CanBeNull="false" />
              <Column Name="Legacy" Type="System.String" DbType="NVarChar(10)" />
            </Type>
          </Table>
          <Table Name="dbo.Customers" Member="Customers">
            <Type Name="Customer">
              <Column Name="Id" Type="System.Int32" DbType="Int NOT NULL" IsPrimaryKey="true" CanBeNull="false" />
            </Type>
          </Table>
        </Database>
        """;

    private static DbmlDatabase Parse(string xml) => DbmlReader.Read(Parser.ParseText(xml));

    private static DbColumnSchema Column(
        string name, string sqlType, int ordinal,
        bool nullable = true, bool identity = false, bool primaryKey = false, int? maxLength = null) =>
        new(name, sqlType, nullable, primaryKey, identity, IsComputed: false, IsRowVersion: false,
            ordinal, maxLength, Precision: null, Scale: null);

    /// <summary>The Orders table as the database has it: one column added and one gone.</summary>
    private static DbTableSchema OrdersSchema() => new("dbo", "Orders",
    [
        Column("Id", "int", 1, nullable: false, identity: true, primaryKey: true),
        Column("CustomerId", "int", 2, nullable: false),
        Column("PlacedOn", "datetime2", 3, nullable: false),
    ]);

    [Fact]
    public void AColumnTheDatabaseHasAndTheModelDoesNotIsAdded()
    {
        var database = Parse(TwoTables);
        var plan = DbmlRefreshPlanner.Plan(
            database.TableNamed("dbo.Orders")!, OrdersSchema(), [], database);

        var added = Assert.Single(plan.Added);

        Assert.Equal("PlacedOn", added.Name);
        Assert.Equal("System.DateTime", added.ClrType);
        Assert.Equal("DateTime2 NOT NULL", added.DbType);
        Assert.False(added.CanBeNull);
    }

    [Fact]
    public void AColumnTheModelHasAndTheDatabaseDoesNotIsListedForRemovalRatherThanRemoved()
    {
        // The removals are the reason the plan exists as a value: dropping a <Column> deletes a
        // property the solution may be full of references to.
        var database = Parse(TwoTables);
        var plan = DbmlRefreshPlanner.Plan(
            database.TableNamed("dbo.Orders")!, OrdersSchema(), [], database);

        Assert.Equal("Legacy", Assert.Single(plan.Removed).Name);
    }

    [Fact]
    public void AColumnThatMatchesIsLeftAlone()
    {
        var database = Parse(TwoTables);
        var plan = DbmlRefreshPlanner.Plan(
            database.TableNamed("dbo.Orders")!, OrdersSchema(), [], database);

        Assert.DoesNotContain(plan.Updated, u => u.Existing.Name == "Id");
        Assert.DoesNotContain(plan.Updated, u => u.Existing.Name == "CustomerId");
    }

    [Fact]
    public void AColumnThatChangedIsReportedWithWhatChanged()
    {
        var database = Parse(TwoTables);

        var schema = new DbTableSchema("dbo", "Orders",
        [
            Column("Id", "int", 1, nullable: false, identity: true, primaryKey: true),
            Column("CustomerId", "int", 2), // now nullable
            Column("Legacy", "nvarchar", 3, maxLength: 10),
        ]);

        var plan = DbmlRefreshPlanner.Plan(
            database.TableNamed("dbo.Orders")!, schema, [], database);

        var update = Assert.Single(plan.Updated);

        Assert.Equal("CustomerId", update.Existing.Name);
        Assert.Contains(update.Changes, c => c.Contains("CanBeNull"));
        Assert.Contains(update.Changes, c => c.Contains("DbType"));
    }

    [Fact]
    public void AnAssociationPairIsGeneratedWhenBothTypesExist()
    {
        var database = Parse(TwoTables);

        DbForeignKey[] keys =
        [
            new("FK_Orders_Customers", "dbo.Orders", ["CustomerId"], "dbo.Customers", ["Id"]),
        ];

        var plan = DbmlRefreshPlanner.Plan(
            database.TableNamed("dbo.Orders")!, OrdersSchema(), keys, database);

        Assert.Equal(2, plan.Associations.Length);

        var child = plan.Associations.Single(a => a.IsForeignKey);
        var parent = plan.Associations.Single(a => !a.IsForeignKey);

        // The child holds the key and gets one parent; the parent gets many children.
        Assert.Equal("Order", child.OwnerTypeName);
        Assert.Equal("Customer", child.Member);
        Assert.Equal("CustomerId", child.ThisKey);
        Assert.Equal("Id", child.OtherKey);

        Assert.Equal("Customer", parent.OwnerTypeName);
        Assert.Equal("Orders", parent.Member);
        Assert.Equal("Id", parent.ThisKey);
        Assert.Equal("CustomerId", parent.OtherKey);
    }

    [Fact]
    public void AnAssociationIsSkippedAndSaidSoWhenTheOtherTypeIsMissing()
    {
        // Writing one end alone would name a class SqlMetal will not generate — a designer that does
        // not compile, from a refresh that reported success.
        var database = Parse(TwoTables);

        DbForeignKey[] keys =
        [
            new("FK_Orders_Couriers", "dbo.Orders", ["CourierId"], "dbo.Couriers", ["Id"]),
        ];

        var plan = DbmlRefreshPlanner.Plan(
            database.TableNamed("dbo.Orders")!, OrdersSchema(), keys, database);

        Assert.Empty(plan.Associations);
        Assert.Contains(plan.Notes, n => n.Contains("dbo.Couriers"));
    }

    /// <summary>The Orders/Customers key, as the database reports it in both directions.</summary>
    private static DbForeignKey[] OrdersToCustomers() =>
    [
        new("FK_Orders_Customers", "dbo.Orders", ["CustomerId"], "dbo.Customers", ["Id"]),
    ];

    /// <summary>Both ends of the key already written into the model, under a renamed constraint.</summary>
    private static string BothEndsMapped() => TwoTables
        .Replace(
            """<Column Name="Legacy" Type="System.String" DbType="NVarChar(10)" />""",
            """<Association Name="Orders_Customers" Member="Customer" ThisKey="CustomerId" OtherKey="Id" Type="Customer" IsForeignKey="true" />""")
        .Replace(
            """<Column Name="Id" Type="System.Int32" DbType="Int NOT NULL" IsPrimaryKey="true" CanBeNull="false" />""",
            """
            <Column Name="Id" Type="System.Int32" DbType="Int NOT NULL" IsPrimaryKey="true" CanBeNull="false" />
                  <Association Name="Orders_Customers" Member="Orders" ThisKey="Id" OtherKey="CustomerId" Type="Order" />
            """);

    [Fact]
    public void ARelationshipTheModelAlreadyMapsIsNotGeneratedAgain()
    {
        // The refresh that reported this: every run added the pair it had added the run before,
        // because the check was on the constraint's name and the name is the one thing a refresh
        // itself rewrites.
        var database = Parse(BothEndsMapped());

        var plan = DbmlRefreshPlanner.Plan(
            database.TableNamed("dbo.Orders")!, OrdersSchema(), OrdersToCustomers(), database);

        Assert.Empty(plan.Associations);
    }

    [Fact]
    public void AnAssociationIsRecognisedByItsKeysRatherThanByItsName()
    {
        // Same relationship, named nothing like the constraint — which is what a developer who
        // renamed it, or a refresh that trimmed the FK_ off it, leaves behind.
        string xml = BothEndsMapped()
            .Replace("""Name="Orders_Customers" """, """Name="PlacedBy" """);

        var database = Parse(xml);

        var plan = DbmlRefreshPlanner.Plan(
            database.TableNamed("dbo.Orders")!, OrdersSchema(), OrdersToCustomers(), database);

        Assert.Empty(plan.Associations);
    }

    [Fact]
    public void AHalfMappedRelationshipGetsOnlyTheEndItIsMissing()
    {
        // Each end is its own element and each is checked on its own, so a model with the child end
        // hand-written gets the collection it lacks rather than a second copy of what it has.
        string xml = TwoTables.Replace(
            """<Column Name="Legacy" Type="System.String" DbType="NVarChar(10)" />""",
            """<Association Name="PlacedBy" Member="Customer" ThisKey="CustomerId" OtherKey="Id" Type="Customer" IsForeignKey="true" />""");

        var database = Parse(xml);

        var plan = DbmlRefreshPlanner.Plan(
            database.TableNamed("dbo.Orders")!, OrdersSchema(), OrdersToCustomers(), database);

        var draft = Assert.Single(plan.Associations);

        Assert.Equal("Customer", draft.OwnerTypeName);
        Assert.False(draft.IsForeignKey);
    }

    [Fact]
    public void TheConstraintsFkPrefixIsNotPartOfTheAssociationName()
    {
        // FK_ says the constraint is a foreign key, which an <Association> says by being one.
        var database = Parse(TwoTables);

        var plan = DbmlRefreshPlanner.Plan(
            database.TableNamed("dbo.Orders")!, OrdersSchema(), OrdersToCustomers(), database);

        Assert.Equal(2, plan.Associations.Length);
        Assert.All(plan.Associations, a => Assert.Equal("Orders_Customers", a.Name));
    }

    [Fact]
    public void AConstraintCalledNothingButFkKeepsTheNameItHas()
    {
        var database = Parse(TwoTables);

        DbForeignKey[] keys =
            [new("FK_", "dbo.Orders", ["CustomerId"], "dbo.Customers", ["Id"])];

        var plan = DbmlRefreshPlanner.Plan(
            database.TableNamed("dbo.Orders")!, OrdersSchema(), keys, database);

        Assert.Equal(2, plan.Associations.Length);
        Assert.All(plan.Associations, a => Assert.Equal("FK_", a.Name));
    }

    [Fact]
    public void TheDeclaredEncodingSurvivesTheWrite()
    {
        // The declaration is text in the tree like everything else, so it comes back out as it
        // went in. A writer that re-emits it from its own encoding turned a file saying utf-8 into
        // one saying utf-16, which was then written to disk as UTF-8.
        var database = Parse(TwoTables);
        var plan = DbmlRefreshPlanner.Plan(
            database.TableNamed("dbo.Orders")!, OrdersSchema(), [], database);

        string written = DbmlWriter.Apply(TwoTables, plan, includeRemovals: false)!;

        Assert.Contains("encoding=\"utf-8\"", written);
        Assert.DoesNotContain("utf-16", written);
    }

    [Fact]
    public void ApplyingAPlanAddsTheColumnAndLeavesEverythingElseByteForByte()
    {
        var database = Parse(TwoTables);
        var plan = DbmlRefreshPlanner.Plan(
            database.TableNamed("dbo.Orders")!, OrdersSchema(), [], database);

        string written = DbmlWriter.Apply(TwoTables, plan, includeRemovals: false)!;

        Assert.Contains("""<Column Name="PlacedOn" Type="System.DateTime" DbType="DateTime2 NOT NULL" CanBeNull="false" />""", written);

        // Kept, because the caller was not told to remove it — and the Customers table, which the
        // plan says nothing about, is untouched.
        Assert.Contains("""Name="Legacy" """, written);
        Assert.Contains("""<Table Name="dbo.Customers" Member="Customers">""", written);
    }

    [Fact]
    public void ARemovalOnlyHappensWhenItWasConfirmed()
    {
        var database = Parse(TwoTables);
        var plan = DbmlRefreshPlanner.Plan(
            database.TableNamed("dbo.Orders")!, OrdersSchema(), [], database);

        Assert.Contains("Legacy", DbmlWriter.Apply(TwoTables, plan, includeRemovals: false)!);
        Assert.DoesNotContain("Legacy", DbmlWriter.Apply(TwoTables, plan, includeRemovals: true)!);
    }

    [Fact]
    public void AnAppliedPlanStillParsesAsTheModelItWas()
    {
        var database = Parse(TwoTables);

        DbForeignKey[] keys =
        [
            new("FK_Orders_Customers", "dbo.Orders", ["CustomerId"], "dbo.Customers", ["Id"]),
        ];

        var plan = DbmlRefreshPlanner.Plan(
            database.TableNamed("dbo.Orders")!, OrdersSchema(), keys, database);

        var written = Parse(DbmlWriter.Apply(TwoTables, plan, includeRemovals: true)!);

        var order = written.AllTypes().Single(t => t.Name == "Order");
        var customer = written.AllTypes().Single(t => t.Name == "Customer");

        Assert.Equal(["Id", "CustomerId", "PlacedOn"], order.Columns.Select(c => c.Name));
        Assert.Equal("Customer", Assert.Single(order.Associations).Member);
        Assert.Equal("Orders", Assert.Single(customer.Associations).Member);
    }

    [Fact]
    public void AModelThatDoesNotParseIsNotRewritten()
    {
        // Overwriting a file the user is mid-edit in would take their edit with it.
        var database = Parse(TwoTables);
        var plan = DbmlRefreshPlanner.Plan(
            database.TableNamed("dbo.Orders")!, OrdersSchema(), [], database);

        Assert.Null(DbmlWriter.Apply("<Database><Table", plan, includeRemovals: false));
    }

    [Fact]
    public void AnUpToDateTablePlansNothing()
    {
        var database = Parse(TwoTables);

        var schema = new DbTableSchema("dbo", "Customers",
            [Column("Id", "int", 1, nullable: false, primaryKey: true)]);

        var plan = DbmlRefreshPlanner.Plan(
            database.TableNamed("dbo.Customers")!, schema, [], database);

        Assert.True(plan.IsEmpty);
        Assert.Contains("up to date", plan.Summary);
    }

    [Fact]
    public void TwoKeysBetweenTheSameTablesDoNotBothTakeTheSameMemberName()
    {
        // An order with a billing address and a shipping address is ordinary, and both ends would
        // otherwise be called the same thing — a designer with two properties of one name in it.
        var database = Parse(TwoTables);

        DbForeignKey[] keys =
        [
            new("FK_Orders_Billing", "dbo.Orders", ["CustomerId"], "dbo.Customers", ["Id"]),
            new("FK_Orders_Shipping", "dbo.Orders", ["CustomerId"], "dbo.Customers", ["Id"]),
        ];

        var plan = DbmlRefreshPlanner.Plan(
            database.TableNamed("dbo.Orders")!, OrdersSchema(), keys, database);

        var members = plan.Associations
            .Where(a => a.OwnerTypeName == "Order")
            .Select(a => a.Member)
            .ToImmutableArray();

        Assert.Equal(2, members.Length);
        Assert.Equal(members.Length, members.Distinct().Count());
    }
}

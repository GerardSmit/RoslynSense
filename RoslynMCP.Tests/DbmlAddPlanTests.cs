using System.Collections.Immutable;
using Microsoft.Language.Xml;
using RoslynMCP.Languages.Dbml.Core;
using RoslynMCP.Services.Database;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Adding whole tables, views and functions to a model, from the picker's list to the write.
/// </summary>
/// <remarks>
/// Pure throughout, like <see cref="DbmlRefreshPlanTests"/> and for the same reason: the planner
/// takes described catalogue objects and the writer takes text, so no test needs a server.
/// </remarks>
public class DbmlAddPlanTests
{
    private const string Ns = "http://schemas.microsoft.com/linqtosql/dbml/2007";

    private const string OneTable = $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Database Name="Shop" Class="ShopDataContext" xmlns="{Ns}">
          <Table Name="dbo.Orders" Member="Orders">
            <Type Name="Order">
              <Column Name="Id" Type="System.Int32" DbType="Int NOT NULL IDENTITY" IsPrimaryKey="true" IsDbGenerated="true" CanBeNull="false" />
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

    private static DbTableSchema CustomersSchema() => new("dbo", "Customer",
    [
        Column("Id", "int", 1, nullable: false, identity: true, primaryKey: true),
        Column("Name", "nvarchar", 2, nullable: false, maxLength: 50),
    ]);

    // ---- What the picker offers -----------------------------------------------------------------

    [Fact]
    public void AModelledTableIsNotOfferedAgain()
    {
        var missing = DbmlAddPlanner.Missing(Parse(OneTable),
        [
            new DbSchemaObject("dbo", "Orders", DbSchemaObjectKind.Table),
            new DbSchemaObject("dbo", "Customer", DbSchemaObjectKind.Table),
        ]);

        Assert.Equal("dbo.Customer", Assert.Single(missing).QualifiedName);
    }

    [Fact]
    public void AnUnqualifiedModelNameCountsAsTheDboObject()
    {
        // Models commonly write Name="Orders" where the database says dbo.Orders.
        const string unqualified = $"""
            <Database Name="Shop" xmlns="{Ns}">
              <Table Name="Orders" Member="Orders">
                <Type Name="Order" />
              </Table>
            </Database>
            """;

        var missing = DbmlAddPlanner.Missing(Parse(unqualified),
            [new DbSchemaObject("dbo", "Orders", DbSchemaObjectKind.Table)]);

        Assert.Empty(missing);
    }

    [Fact]
    public void AnUnqualifiedModelNameDoesNotClaimAnotherSchemasObject()
    {
        const string unqualified = $"""
            <Database Name="Shop" xmlns="{Ns}">
              <Table Name="Orders" Member="Orders">
                <Type Name="Order" />
              </Table>
            </Database>
            """;

        var missing = DbmlAddPlanner.Missing(Parse(unqualified),
            [new DbSchemaObject("audit", "Orders", DbSchemaObjectKind.Table)]);

        Assert.Equal("audit.Orders", Assert.Single(missing).QualifiedName);
    }

    [Fact]
    public void AModelledFunctionIsNotOfferedAgain()
    {
        const string withFunction = $"""
            <Database Name="Shop" xmlns="{Ns}">
              <Function Name="dbo.GetTotal" Method="GetTotal" IsComposable="true">
                <Return Type="System.Decimal" />
              </Function>
            </Database>
            """;

        var missing = DbmlAddPlanner.Missing(Parse(withFunction),
        [
            new DbSchemaObject("dbo", "GetTotal", DbSchemaObjectKind.ScalarFunction),
            new DbSchemaObject("dbo", "GetOrders", DbSchemaObjectKind.StoredProcedure),
        ]);

        Assert.Equal("dbo.GetOrders", Assert.Single(missing).QualifiedName);
    }

    // ---- Planning a table -----------------------------------------------------------------------

    [Fact]
    public void APlannedTableIsNamedTheWayTheDesignerWouldNameIt()
    {
        var draft = Assert.Single(
            DbmlAddPlanner.PlanTables([CustomersSchema()], Parse(OneTable)));

        Assert.Equal("dbo.Customer", draft.Name);
        Assert.Equal("Customer", draft.TypeName);
        Assert.Equal("Customers", draft.Member);
        Assert.Equal(2, draft.Columns.Length);
    }

    [Fact]
    public void AnAlreadyPluralNameIsNotPluralisedAgain()
    {
        var schema = new DbTableSchema("dbo", "Invoices", [Column("Id", "int", 1)]);

        var draft = Assert.Single(DbmlAddPlanner.PlanTables([schema], Parse(OneTable)));

        Assert.Equal("Invoices", draft.Member);
    }

    [Fact]
    public void ATypeNameTheModelAlreadyGeneratesGetsASuffix()
    {
        // The model already generates a class Order; a second <Type Name="Order"> would be a
        // designer that does not compile.
        var schema = new DbTableSchema("archive", "Order", [Column("Id", "int", 1)]);

        var draft = Assert.Single(DbmlAddPlanner.PlanTables([schema], Parse(OneTable)));

        Assert.Equal("Order1", draft.TypeName);
    }

    [Fact]
    public void TwoTablesPlannedTogetherDoNotClaimTheSameTypeName()
    {
        var first = new DbTableSchema("dbo", "Invoice", [Column("Id", "int", 1)]);
        var second = new DbTableSchema("archive", "Invoice", [Column("Id", "int", 1)]);

        var drafts = DbmlAddPlanner.PlanTables([first, second], Parse(OneTable));

        Assert.Equal("Invoice", drafts[0].TypeName);
        Assert.Equal("Invoice1", drafts[1].TypeName);
    }

    // ---- Planning a function --------------------------------------------------------------------

    [Fact]
    public void AScalarFunctionGetsItsReturnAndIsComposable()
    {
        var schema = new DbFunctionSchema(
            "dbo", "GetTotal", DbSchemaObjectKind.ScalarFunction,
            [new DbParameterSchema("orderId", "int", IsOutput: false, null, null, null)],
            ReturnValue: new DbParameterSchema(
                "", "decimal", IsOutput: false, null, Precision: 18, Scale: 2),
            ResultColumns: []);

        var draft = Assert.Single(DbmlAddPlanner.PlanFunctions([schema], Parse(OneTable)));

        Assert.Equal("GetTotal", draft.Method);
        Assert.True(draft.IsComposable);
        Assert.Equal("System.Decimal", draft.ReturnClrType);
        Assert.Equal("Decimal(18,2)", draft.ReturnDbType);
        Assert.Null(draft.ElementTypeName);

        var parameter = Assert.Single(draft.Parameters);
        Assert.Equal("orderId", parameter.Name);
        Assert.Equal("System.Int32", parameter.ClrType);
        Assert.Equal("Int", parameter.DbType);
    }

    [Fact]
    public void AProcedureWithRowsGetsAnElementTypeInsteadOfAReturn()
    {
        var schema = new DbFunctionSchema(
            "dbo", "GetCustomers", DbSchemaObjectKind.StoredProcedure,
            Parameters: [], ReturnValue: null,
            ResultColumns: [Column("Id", "int", 1, nullable: false)]);

        var draft = Assert.Single(DbmlAddPlanner.PlanFunctions([schema], Parse(OneTable)));

        Assert.False(draft.IsComposable);
        Assert.Null(draft.ReturnClrType);
        Assert.Equal("GetCustomersResult", draft.ElementTypeName);
        Assert.Equal("Id", Assert.Single(draft.ElementColumns).Name);
    }

    [Fact]
    public void AProcedureWithoutRowsStillReturnsItsCode()
    {
        // LINQ to SQL gives every procedure an int return; without it the element would name a
        // method SqlMetal cannot give a return type to.
        var schema = new DbFunctionSchema(
            "dbo", "Cleanup", DbSchemaObjectKind.StoredProcedure,
            Parameters: [], ReturnValue: null, ResultColumns: []);

        var draft = Assert.Single(DbmlAddPlanner.PlanFunctions([schema], Parse(OneTable)));

        Assert.Equal("System.Int32", draft.ReturnClrType);
        Assert.Null(draft.ElementTypeName);
    }

    [Fact]
    public void AnOutputParameterIsWrittenInOut()
    {
        var schema = new DbFunctionSchema(
            "dbo", "Tally", DbSchemaObjectKind.StoredProcedure,
            [new DbParameterSchema("total", "int", IsOutput: true, null, null, null)],
            ReturnValue: null, ResultColumns: []);

        var draft = Assert.Single(DbmlAddPlanner.PlanFunctions([schema], Parse(OneTable)));

        Assert.Equal("InOut", Assert.Single(draft.Parameters).Direction);
    }

    // ---- The write ------------------------------------------------------------------------------

    [Fact]
    public void AnAddedTableIsWrittenAfterTheLastTableIndentedLikeItsNeighbours()
    {
        var drafts = DbmlAddPlanner.PlanTables([CustomersSchema()], Parse(OneTable));

        string? result = DbmlWriter.AddObjects(OneTable, drafts, []);

        Assert.NotNull(result);
        Assert.Contains(
            """
              <Table Name="dbo.Customer" Member="Customers">
                <Type Name="Customer">
                  <Column Name="Id" Type="System.Int32" DbType="Int NOT NULL IDENTITY" IsPrimaryKey="true" IsDbGenerated="true" CanBeNull="false" />
                  <Column Name="Name" Type="System.String" DbType="NVarChar(50) NOT NULL" CanBeNull="false" />
                </Type>
              </Table>
            """.ReplaceLineEndings(), result.ReplaceLineEndings());

        // The new table follows the existing one rather than preceding it.
        Assert.True(
            result!.IndexOf("dbo.Orders", StringComparison.Ordinal)
            < result.IndexOf("dbo.Customer", StringComparison.Ordinal));
    }

    [Fact]
    public void TheRestOfTheFileIsUntouched()
    {
        var drafts = DbmlAddPlanner.PlanTables([CustomersSchema()], Parse(OneTable));

        string? result = DbmlWriter.AddObjects(OneTable, drafts, []);

        // Everything the file already said survives character for character.
        Assert.NotNull(result);
        foreach (string line in OneTable.Split('\n'))
            Assert.Contains(line.TrimEnd('\r'), result!);
    }

    [Fact]
    public void AnAddedFunctionGoesAfterTheTables()
    {
        var schema = new DbFunctionSchema(
            "dbo", "GetTotal", DbSchemaObjectKind.ScalarFunction,
            Parameters: [],
            ReturnValue: new DbParameterSchema("", "int", IsOutput: false, null, null, null),
            ResultColumns: []);

        var drafts = DbmlAddPlanner.PlanFunctions([schema], Parse(OneTable));

        string? result = DbmlWriter.AddObjects(OneTable, [], drafts);

        Assert.NotNull(result);
        Assert.Contains(
            """
              <Function Name="dbo.GetTotal" Method="GetTotal" IsComposable="true">
                <Return Type="System.Int32" DbType="Int" />
              </Function>
            """.ReplaceLineEndings(), result!.ReplaceLineEndings());
        Assert.True(
            result.IndexOf("</Table>", StringComparison.Ordinal)
            < result.IndexOf("<Function", StringComparison.Ordinal));
    }

    [Fact]
    public void AProceduresRowsBecomeAnElementType()
    {
        var schema = new DbFunctionSchema(
            "dbo", "GetCustomers", DbSchemaObjectKind.StoredProcedure,
            Parameters: [new DbParameterSchema("minAge", "int", IsOutput: false, null, null, null)],
            ReturnValue: null,
            ResultColumns: [Column("Id", "int", 1, nullable: false)]);

        var drafts = DbmlAddPlanner.PlanFunctions([schema], Parse(OneTable));

        string? result = DbmlWriter.AddObjects(OneTable, [], drafts);

        Assert.NotNull(result);
        Assert.Contains(
            """
              <Function Name="dbo.GetCustomers" Method="GetCustomers">
                <Parameter Name="minAge" Type="System.Int32" DbType="Int" />
                <ElementType Name="GetCustomersResult">
                  <Column Name="Id" Type="System.Int32" DbType="Int NOT NULL" CanBeNull="false" />
                </ElementType>
              </Function>
            """.ReplaceLineEndings(), result!.ReplaceLineEndings());
    }

    [Fact]
    public void AnAddedTableRoundTripsWithoutChurn()
    {
        // The written table, described again from the same schema, must plan an empty refresh —
        // otherwise every add is followed by a refresh that reports changes nobody made.
        var schema = CustomersSchema();
        var drafts = DbmlAddPlanner.PlanTables([schema], Parse(OneTable));

        string result = DbmlWriter.AddObjects(OneTable, drafts, [])!;
        var database = Parse(result);
        var table = database.TableNamed("dbo.Customer");

        Assert.NotNull(table);

        var plan = DbmlRefreshPlanner.Plan(table!, schema, [], database);

        Assert.True(plan.IsEmpty, plan.Summary);
    }

    [Fact]
    public void AnAddedTablesForeignKeyBecomesAnAssociationPair()
    {
        // The command's second pass: the table is written first, then its keys are planned against
        // the extended text, where both <Type>s now exist.
        var schema = new DbTableSchema("dbo", "OrderLines",
        [
            Column("Id", "int", 1, nullable: false, primaryKey: true),
            Column("OrderId", "int", 2, nullable: false),
        ]);

        string extended = DbmlWriter.AddObjects(
            OneTable, DbmlAddPlanner.PlanTables([schema], Parse(OneTable)), [])!;

        var database = Parse(extended);
        var plan = DbmlRefreshPlanner.Plan(
            database.TableNamed("dbo.OrderLines")!, schema,
            [new DbForeignKey("FK_OrderLines_Orders", "dbo.OrderLines", ["OrderId"], "dbo.Orders", ["Id"])],
            database);

        Assert.Equal(2, plan.Associations.Length);

        string? final = DbmlWriter.Apply(extended, plan, includeRemovals: false);

        Assert.NotNull(final);
        Assert.Contains("<Association Name=\"OrderLines_Orders\"", final!);
    }
}

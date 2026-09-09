using Microsoft.Data.Sqlite;
using RoslynMCP.Services;
using RoslynMCP.Services.Database;
using RoslynMCP.Tools;
using Xunit;

namespace RoslynMCP.Tests;

public sealed class DatabaseParameterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParametersPreserveIntegerAndDecimalPrecisionAndScalarTypes(bool execute)
    {
        var provider = new RecordingProvider();
        const string json = """
            {"@id":9007199254740993,"@max":9223372036854775807,"@min":-9223372036854775808,
             "@amount":1234567890.123456789,"@flag":true,"@missing":null,
             "@text":"O'Brien; DROP TABLE users; -- 😀"}
            """;

        string output = await Invoke(execute, provider, json);

        Assert.DoesNotContain("Error", output);
        Assert.Equal(1, provider.Calls);
        var values = Assert.IsType<Dictionary<string, object?>>(provider.Parameters);
        Assert.Equal(9007199254740993L, Assert.IsType<long>(values["@id"]));
        Assert.Equal(long.MaxValue, Assert.IsType<long>(values["@max"]));
        Assert.Equal(long.MinValue, Assert.IsType<long>(values["@min"]));
        Assert.Equal(1234567890.123456789m, Assert.IsType<decimal>(values["@amount"]));
        Assert.True(Assert.IsType<bool>(values["@flag"]));
        Assert.Null(values["@missing"]);
        Assert.Equal("O'Brien; DROP TABLE users; -- 😀", values["@text"]);
        Assert.Equal("SELECT @id", provider.Sql);
    }

    [Theory]
    [InlineData(false, "[]")]
    [InlineData(true, "[]")]
    [InlineData(false, "{\"@id\":1e400}")]
    [InlineData(true, "{\"@id\":1e400}")]
    public async Task InvalidParametersNeverReachTheDatabase(bool execute, string json)
    {
        var provider = new RecordingProvider();

        string output = await Invoke(execute, provider, json);

        Assert.StartsWith("Error:", output);
        Assert.Equal(0, provider.Calls);
    }

    [Theory]
    [InlineData("1e-100", 1e-100)]
    [InlineData("1e100", 1e100)]
    [InlineData("1.5e-28", 1.5e-28)]
    [InlineData("-1.5e-28", -1.5e-28)]
    public async Task FiniteNumbersThatDecimalCannotPreserveRemainFloatingPoint(string jsonNumber, double expected)
    {
        var provider = new RecordingProvider();
        await Invoke(false, provider, "{\"@value\":" + jsonNumber + "}");
        Assert.Equal(expected, Assert.IsType<double>(provider.Parameters!["@value"]));
    }

    [Theory]
    [InlineData("query")]
    [InlineData("execute")]
    [InlineData("tables")]
    [InlineData("describe")]
    public async Task ProviderCancellationIsPropagatedRatherThanReportedAsSqlFailure(string operation)
    {
        var provider = new RecordingProvider();
        var registry = new DbConnectionRegistry([provider]);
        var formatter = new MarkdownFormatter();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Task<string> result = operation switch
        {
            "query" => DatabaseTool.DbQuery("test", "SELECT 1", registry, formatter,
                new ExecutionPlanStore(), cancellationToken: cancellation.Token),
            "execute" => DatabaseTool.DbExecute("test", "UPDATE t SET v=1", registry, formatter,
                cancellationToken: cancellation.Token),
            "tables" => DatabaseTool.DbListTables("test", registry, formatter,
                cancellationToken: cancellation.Token),
            _ => DatabaseTool.DbDescribeTable("test", "t", registry, formatter,
                cancellationToken: cancellation.Token),
        };

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result);
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public async Task SqliteKeepsFractionalJsonParametersNumericWithoutChangingIntegerOrTextParameters()
    {
        var registry = new DbConnectionRegistry([new SqliteDbProvider("test", ":memory:")]);

        string output = await DatabaseTool.DbQuery("test",
            "SELECT typeof(@fraction) AS kind, @fraction < 2 AS comparison, typeof(@id) AS id_kind, " +
            "@id AS id, typeof(@text) AS text_kind, @text AS literal",
            registry, new MarkdownFormatter(), new ExecutionPlanStore(),
            parameters: """{"@fraction":1.5,"@id":9007199254740993,"@text":"1.5"}""");

        Assert.DoesNotContain("Error", output);
        Assert.Contains("| real | 1 | integer | 9007199254740993 | text | 1.5 |", output);
    }

    [Fact]
    public async Task SqliteExecuteUsesNumericFractionalParametersInPredicates()
    {
        string connectionString = $"Data Source=parameter-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();
        await using (var seed = keepAlive.CreateCommand())
        {
            seed.CommandText = "CREATE TABLE items (id INTEGER); INSERT INTO items VALUES (1);";
            await seed.ExecuteNonQueryAsync();
        }
        var provider = new SqliteDbProvider("test", connectionString);

        string output = await DatabaseTool.DbExecute("test", "DELETE FROM items WHERE @fraction < 2",
            new DbConnectionRegistry([provider]), new MarkdownFormatter(), """{"@fraction":1.5}""");

        Assert.DoesNotContain("Error", output);
        var remaining = await provider.ExecuteQueryAsync("SELECT COUNT(*) FROM items", null, 1, false, default);
        Assert.Equal("0", Assert.Single(remaining.Rows)[0]);
    }

    [Fact]
    public async Task SqliteReceivesTheExactLargeIntegerAndLiteralText()
    {
        var registry = new DbConnectionRegistry([new SqliteDbProvider("test", ":memory:")]);

        string output = await DatabaseTool.DbQuery("test", "SELECT typeof(@id) AS kind, @id AS id, @text AS literal",
            registry, new MarkdownFormatter(), new ExecutionPlanStore(),
            parameters: """{"@id":9007199254740993,"@text":"O'Brien; SELECT 'not executed'"}""");

        Assert.DoesNotContain("Error", output);
        Assert.Contains("integer", output);
        Assert.Contains("9007199254740993", output);
        Assert.Contains("O'Brien; SELECT 'not executed'", output);
    }

    private static Task<string> Invoke(bool execute, RecordingProvider provider, string json)
    {
        var registry = new DbConnectionRegistry([provider]);
        return execute
            ? DatabaseTool.DbExecute("test", "SELECT @id", registry, new MarkdownFormatter(), json)
            : DatabaseTool.DbQuery("test", "SELECT @id", registry, new MarkdownFormatter(), new ExecutionPlanStore(), json);
    }

    private sealed class RecordingProvider : IDbProvider
    {
        public string Alias => "test";
        public string ProviderName => "test";
        public PlanFormat? PlanFormat => null;
        public int Calls { get; private set; }
        public string? Sql { get; private set; }
        public Dictionary<string, object?>? Parameters { get; private set; }

        public Task<DbQueryResult> ExecuteQueryAsync(string sql, Dictionary<string, object?>? parameters,
            int maxRows, bool capturePlan, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            Sql = sql;
            Parameters = parameters;
            return Task.FromResult(new DbQueryResult(["value"], [["ok"]], false, TimeSpan.Zero));
        }

        public Task<int> ExecuteNonQueryAsync(string sql, Dictionary<string, object?>? parameters, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            Sql = sql;
            Parameters = parameters;
            return Task.FromResult(1);
        }

        public Task<DbSchemaResult> GetTablesAsync(string? schema, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new DbSchemaResult([], []));
        }

        public Task<DbSchemaResult> DescribeTableAsync(string tableName, CancellationToken ct) => GetTablesAsync(null, ct);
    }
}

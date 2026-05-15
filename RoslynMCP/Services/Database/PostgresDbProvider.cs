using System.Data.Common;
using System.Globalization;
using System.Text.Json.Nodes;
using Npgsql;

namespace RoslynMCP.Services.Database;

public sealed class PostgresDbProvider : DbProviderBase
{
    private bool? _hypopgInstalled;

    public PostgresDbProvider(string alias, string connectionString)
        : base(alias, "psql", connectionString) { }

    protected override DbConnection CreateConnection() => new NpgsqlConnection(ConnectionString);

    protected override DbCommand CreateCommand(string sql, DbConnection conn) =>
        new NpgsqlCommand(sql, (NpgsqlConnection)conn);

    protected override string PrepareSqlForPlanCapture(string sql, bool capturePlan) =>
        capturePlan
            ? "EXPLAIN (ANALYZE, BUFFERS, COSTS, VERBOSE, FORMAT JSON) " + sql
            : sql;

    protected override bool IsPlanResultColumn(string columnName) =>
        string.Equals(columnName, "QUERY PLAN", StringComparison.OrdinalIgnoreCase);

    public override PlanFormat? PlanFormat => Services.Database.PlanFormat.Json;

    public async Task<bool> IsHypopgInstalledAsync(CancellationToken ct)
    {
        if (_hypopgInstalled is { } cached) return cached;
        await using var conn = (NpgsqlConnection)CreateConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT 1 FROM pg_extension WHERE extname = 'hypopg'", conn);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        _hypopgInstalled = result is not null;
        return _hypopgInstalled.Value;
    }

    public async Task<List<HypopgEvaluation>> EvaluateHypotheticalIndexesAsync(
        string sql,
        Dictionary<string, object?>? parameters,
        IReadOnlyList<(string Table, string Column)> candidates,
        CancellationToken ct)
    {
        var results = new List<HypopgEvaluation>();
        if (candidates.Count == 0) return results;

        await using var conn = (NpgsqlConnection)CreateConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);

        var baselineCost = await GetEstimatedCostAsync(conn, sql, parameters, ct).ConfigureAwait(false);
        if (baselineCost is null) return results;

        foreach (var (table, column) in candidates.Distinct())
        {
            await SafeExecuteAsync(conn, "SELECT hypopg_reset()", ct).ConfigureAwait(false);

            var quotedTable = QuoteIdent(table);
            var quotedCol = QuoteIdent(column);
            var createStmt = $"CREATE INDEX ON {quotedTable}({quotedCol})";
            string? createdName = null;
            try
            {
                await using var createCmd = new NpgsqlCommand(
                    "SELECT indexname FROM hypopg_create_index($1)", conn);
                createCmd.Parameters.AddWithValue(createStmt);
                createdName = (await createCmd.ExecuteScalarAsync(ct).ConfigureAwait(false))?.ToString();
            }
            catch
            {
                continue;
            }
            if (createdName is null) continue;

            var hypoCost = await GetEstimatedCostAsync(conn, sql, parameters, ct).ConfigureAwait(false);
            var chose = await PlannerChoseHypotheticalAsync(conn, sql, parameters, createdName, ct).ConfigureAwait(false);

            if (hypoCost is { } h)
            {
                var reduction = baselineCost.Value > 0
                    ? (baselineCost.Value - h) / baselineCost.Value * 100
                    : 0;
                results.Add(new HypopgEvaluation(table, column, baselineCost.Value, h, reduction, chose));
            }
        }

        await SafeExecuteAsync(conn, "SELECT hypopg_reset()", ct).ConfigureAwait(false);
        return results;
    }

    private static async Task<double?> GetEstimatedCostAsync(
        NpgsqlConnection conn, string sql, Dictionary<string, object?>? parameters, CancellationToken ct)
    {
        var explainSql = "EXPLAIN (FORMAT JSON, COSTS) " + sql;
        await using var cmd = new NpgsqlCommand(explainSql, conn);
        BindParameters(cmd, parameters);
        var raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        var text = raw?.ToString();
        if (string.IsNullOrEmpty(text)) return null;
        var node = JsonNode.Parse(text);
        var first = node is JsonArray arr && arr.Count > 0 ? arr[0] : node;
        var plan = first?["Plan"];
        if (plan is null) return null;
        var cost = plan["Total Cost"];
        if (cost is null) return null;
        try { return (double)cost.AsValue(); }
        catch { return null; }
    }

    private static async Task<bool> PlannerChoseHypotheticalAsync(
        NpgsqlConnection conn, string sql, Dictionary<string, object?>? parameters,
        string indexName, CancellationToken ct)
    {
        var explainSql = "EXPLAIN (FORMAT JSON) " + sql;
        await using var cmd = new NpgsqlCommand(explainSql, conn);
        BindParameters(cmd, parameters);
        var raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        var text = raw?.ToString();
        return text is not null && text.Contains(indexName, StringComparison.Ordinal);
    }

    private static async Task SafeExecuteAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        try
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch { }
    }

    private static string QuoteIdent(string ident)
    {
        return "\"" + ident.Replace("\"", "\"\"") + "\"";
    }

    public override Task<DbSchemaResult> GetTablesAsync(string? schema, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(schema))
        {
            const string sql =
                "SELECT table_schema, table_name, table_type FROM information_schema.tables " +
                "WHERE table_schema NOT IN ('pg_catalog','information_schema') " +
                "ORDER BY table_schema, table_name";
            return RunSchemaQueryAsync(sql, null, ct);
        }

        const string sqlWithSchema =
            "SELECT table_schema, table_name, table_type FROM information_schema.tables " +
            "WHERE table_schema = @schema ORDER BY table_name";
        return RunSchemaQueryAsync(sqlWithSchema,
            new Dictionary<string, object?> { ["@schema"] = schema }, ct);
    }

    public override Task<DbSchemaResult> DescribeTableAsync(string tableName, CancellationToken ct)
    {
        // Accept "schema.table" or just "table".
        string? schema = null;
        var name = tableName;
        var dot = tableName.IndexOf('.');
        if (dot > 0)
        {
            schema = tableName[..dot];
            name = tableName[(dot + 1)..];
        }

        var sql =
            "SELECT column_name, data_type, is_nullable, column_default " +
            "FROM information_schema.columns " +
            "WHERE table_name = @name" +
            (schema is null ? "" : " AND table_schema = @schema") +
            " ORDER BY ordinal_position";

        var parameters = new Dictionary<string, object?> { ["@name"] = name };
        if (schema is not null) parameters["@schema"] = schema;
        return RunSchemaQueryAsync(sql, parameters, ct);
    }
}

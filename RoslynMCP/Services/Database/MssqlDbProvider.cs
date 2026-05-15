using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace RoslynMCP.Services.Database;

public sealed class MssqlDbProvider : DbProviderBase
{
    public MssqlDbProvider(string alias, string connectionString)
        : base(alias, "mssql", connectionString) { }

    protected override DbConnection CreateConnection() => new SqlConnection(ConnectionString);

    protected override DbCommand CreateCommand(string sql, DbConnection conn) =>
        new SqlCommand(sql, (SqlConnection)conn);

    protected override string PrepareSqlForPlanCapture(string sql, bool capturePlan) =>
        capturePlan ? "SET STATISTICS XML ON;\n" + sql : sql;

    public override PlanFormat? PlanFormat => Services.Database.PlanFormat.Xml;

    public override Task<DbSchemaResult> GetTablesAsync(string? schema, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(schema))
        {
            const string sql =
                "SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE FROM INFORMATION_SCHEMA.TABLES " +
                "ORDER BY TABLE_SCHEMA, TABLE_NAME";
            return RunSchemaQueryAsync(sql, null, ct);
        }

        const string sqlWithSchema =
            "SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE FROM INFORMATION_SCHEMA.TABLES " +
            "WHERE TABLE_SCHEMA = @schema ORDER BY TABLE_NAME";
        return RunSchemaQueryAsync(sqlWithSchema,
            new Dictionary<string, object?> { ["@schema"] = schema }, ct);
    }

    public override Task<DbSchemaResult> DescribeTableAsync(string tableName, CancellationToken ct)
    {
        string? schema = null;
        var name = tableName;
        var dot = tableName.IndexOf('.');
        if (dot > 0)
        {
            schema = tableName[..dot];
            name = tableName[(dot + 1)..];
        }

        var sql =
            "SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE, COLUMN_DEFAULT " +
            "FROM INFORMATION_SCHEMA.COLUMNS " +
            "WHERE TABLE_NAME = @name" +
            (schema is null ? "" : " AND TABLE_SCHEMA = @schema") +
            " ORDER BY ORDINAL_POSITION";

        var parameters = new Dictionary<string, object?> { ["@name"] = name };
        if (schema is not null) parameters["@schema"] = schema;
        return RunSchemaQueryAsync(sql, parameters, ct);
    }
}

using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace RoslynMCP.Services.Database;

public sealed class MssqlDbProvider : DbProviderBase
{
    public MssqlDbProvider(string alias, string connectionString)
        : base(alias, "mssql", ApplyDefaults(connectionString)) { }

    /// <summary>
    /// Trusts the server certificate unless the connection string says otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Connection strings here are typically lifted straight from a <c>web.config</c> that a
    /// .NET Framework app uses happily. That app's <c>System.Data.SqlClient</c> defaults to
    /// <c>Encrypt=false</c>; the <c>Microsoft.Data.SqlClient</c> used here has defaulted to
    /// <c>Encrypt=true</c> since v4.0, so the same string suddenly fails certificate validation
    /// against a server with a self-signed certificate — the usual case for a development SQL
    /// Server. Defaulting this restores the behaviour the string was written for.
    /// </para>
    /// <para>
    /// This does weaken transport security: an encrypted connection whose certificate is not
    /// validated is not protected against interception. It is a deliberate development-time
    /// default, and an explicit <c>TrustServerCertificate=False</c> is always respected, which is
    /// how to opt back into validation for a production server.
    /// </para>
    /// </remarks>
    internal static string ApplyDefaults(string connectionString)
    {
        try
        {
            // Presence has to be read from the raw string: SqlConnectionStringBuilder exposes
            // every known keyword whether or not it was supplied, so ContainsKey is always true.
            // A plain DbConnectionStringBuilder keeps only what was actually written.
            var supplied = new System.Data.Common.DbConnectionStringBuilder
            {
                ConnectionString = connectionString,
            };

            var alreadySet = supplied.Keys
                .Cast<string>()
                .Any(key => key.Replace(" ", "").Equals(
                    "trustservercertificate", StringComparison.OrdinalIgnoreCase));

            if (alreadySet)
                return connectionString;

            var builder = new SqlConnectionStringBuilder(connectionString)
            {
                TrustServerCertificate = true,
            };

            return builder.ConnectionString;
        }
        catch (Exception)
        {
            // Unparseable: hand it to SqlClient unchanged so it reports the real problem.
            return connectionString;
        }
    }

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

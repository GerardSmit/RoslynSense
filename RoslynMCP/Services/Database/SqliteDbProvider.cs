using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace RoslynMCP.Services.Database;

public sealed class SqliteDbProvider : DbProviderBase
{
    private static readonly Regex s_safeIdent = new("^[A-Za-z0-9_]+$", RegexOptions.Compiled);
    private readonly bool _isMemory;

    public SqliteDbProvider(string alias, string connectionString)
        : base(alias, "sqlite", NormalizeConnectionString(connectionString, out bool isMemory))
    {
        _isMemory = isMemory;
    }

    private static string NormalizeConnectionString(string raw, out bool isMemory)
    {
        // Accept a bare file path or a full connection string.
        isMemory = raw.Contains(":memory:", StringComparison.OrdinalIgnoreCase);
        if (raw.Contains('=', StringComparison.Ordinal))
            return raw;
        return $"Data Source={raw}";
    }

    protected override DbConnection CreateConnection() => new SqliteConnection(ConnectionString);

    protected override DbCommand CreateCommand(string sql, DbConnection conn) =>
        new SqliteCommand(sql, (SqliteConnection)conn);

    protected override void BindCommandParameters(DbCommand cmd, Dictionary<string, object?>? parameters)
    {
        base.BindCommandParameters(cmd, parameters);
        // SQLite has no decimal numeric storage; Microsoft.Data.Sqlite binds decimal as TEXT.
        // Keep numeric parameters numeric so comparisons with literals retain their meaning.
        foreach (DbParameter parameter in cmd.Parameters)
        {
            if (parameter.Value is decimal number)
                parameter.Value = (double)number;
        }
    }

    protected override async Task OnConnectionOpenedAsync(DbConnection conn, CancellationToken ct)
    {
        if (_isMemory) return;
        await using var cmd = new SqliteCommand("PRAGMA journal_mode=WAL;", (SqliteConnection)conn);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public override Task<DbSchemaResult> GetTablesAsync(string? schema, CancellationToken ct)
    {
        const string sql = "SELECT name, type FROM sqlite_schema " +
                           "WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%' ORDER BY name";
        return RunSchemaQueryAsync(sql, null, ct);
    }

    public override Task<DbSchemaResult> DescribeTableAsync(string tableName, CancellationToken ct)
    {
        if (!s_safeIdent.IsMatch(tableName))
            throw new ArgumentException($"Invalid table name '{tableName}'. Only letters, digits, and underscore allowed.");
        var sql = $"PRAGMA table_info(\"{tableName}\")";
        return RunSchemaQueryAsync(sql, null, ct);
    }
}

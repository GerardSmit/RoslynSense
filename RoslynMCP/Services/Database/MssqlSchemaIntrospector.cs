using System.Collections.Immutable;
using System.Data.Common;

namespace RoslynMCP.Services.Database;

/// <summary>
/// The catalogue half of <see cref="MssqlDbProvider"/>: what a table is, in enough detail to write a
/// <c>.dbml</c> from.
/// </summary>
/// <remarks>
/// <para>
/// Everything is read from <c>sys.*</c> rather than from <c>INFORMATION_SCHEMA</c>. The ISO views
/// carry no identity flag, no computed flag and no row-version flag, and their key views require a
/// three-way join through <c>TABLE_CONSTRAINTS</c> to say which columns a primary key covers — all
/// four of which are single columns in <c>sys.columns</c> and <c>sys.index_columns</c>. The
/// catalogue views also survive a table named with a keyword, which the <c>OBJECT_ID</c> lookup here
/// leans on.
/// </para>
/// <para>
/// Every query is parameterised on the table name and none of it is concatenated. The name arrives
/// from a <c>.dbml</c>'s <c>&lt;Table Name&gt;</c>, which is a file in the workspace and therefore no
/// more trustworthy than any other input; <c>OBJECT_ID(@table)</c> resolves it without it ever
/// reaching the parser as SQL.
/// </para>
/// </remarks>
public sealed partial class MssqlDbProvider : IDbSchemaIntrospector
{
    /// <summary>
    /// Everything a <c>.dbml</c> could model, and nothing the server shipped: <c>sysdiagrams</c> and
    /// the diagramming procedures are marked <c>is_ms_shipped</c> and are exactly what a picker over
    /// this list must not offer.
    /// </summary>
    private const string ObjectListSql = """
        SELECT s.name AS SchemaName, o.name AS ObjectName, o.type AS ObjectType
        FROM sys.objects o
            INNER JOIN sys.schemas s ON s.schema_id = o.schema_id
        WHERE o.type IN ('U', 'V', 'FN', 'IF', 'TF', 'P') AND o.is_ms_shipped = 0
        ORDER BY s.name, o.name
        """;

    private const string RoutineSql = """
        SELECT s.name AS SchemaName, o.name AS ObjectName, o.type AS ObjectType
        FROM sys.objects o
            INNER JOIN sys.schemas s ON s.schema_id = o.schema_id
        WHERE o.object_id = OBJECT_ID(@table) AND o.type IN ('FN', 'IF', 'TF', 'P')
        """;

    private const string ParameterSql = """
        SELECT
            p.name          AS ParameterName,
            ty.name         AS TypeName,
            p.is_output     AS IsOutput,
            p.max_length    AS MaxLength,
            p.precision     AS [Precision],
            p.scale         AS Scale,
            p.parameter_id  AS Ordinal
        FROM sys.parameters p
            INNER JOIN sys.types ty ON ty.user_type_id = p.user_type_id
        WHERE p.object_id = OBJECT_ID(@table)
        ORDER BY p.parameter_id
        """;

    /// <summary>
    /// A procedure's first result set, statically analysed. <c>QUOTENAME</c> rather than
    /// concatenation because the routine's name arrives from a file in the workspace; resolving it to
    /// an <c>object_id</c> first and quoting what the catalogue says the parts are called is what
    /// keeps the name from ever reaching the parser as SQL.
    /// </summary>
    private const string ResultSetSql = """
        DECLARE @sql nvarchar(max) =
            N'EXEC ' + QUOTENAME(OBJECT_SCHEMA_NAME(OBJECT_ID(@table)))
                     + N'.' + QUOTENAME(OBJECT_NAME(OBJECT_ID(@table)));
        EXEC sp_describe_first_result_set @tsql = @sql;
        """;

    private const string ColumnSql = """
        SELECT
            s.name          AS SchemaName,
            t.name          AS TableName,
            c.name          AS ColumnName,
            ty.name         AS TypeName,
            c.is_nullable   AS IsNullable,
            c.is_identity   AS IsIdentity,
            c.is_computed   AS IsComputed,
            c.max_length    AS MaxLength,
            c.precision     AS [Precision],
            c.scale         AS Scale,
            c.column_id     AS Ordinal,
            CASE WHEN pk.column_id IS NULL THEN 0 ELSE 1 END AS IsPrimaryKey
        FROM sys.columns c
            INNER JOIN sys.objects t  ON t.object_id = c.object_id
                                      AND t.type IN ('U', 'V', 'IF', 'TF')
            INNER JOIN sys.schemas s  ON s.schema_id = t.schema_id
            INNER JOIN sys.types   ty ON ty.user_type_id = c.user_type_id
            LEFT JOIN (
                SELECT ic.object_id, ic.column_id
                FROM sys.index_columns ic
                    INNER JOIN sys.indexes i
                        ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                WHERE i.is_primary_key = 1
            ) pk ON pk.object_id = c.object_id AND pk.column_id = c.column_id
        WHERE c.object_id = OBJECT_ID(@table)
        ORDER BY c.column_id
        """;

    /// <summary>
    /// Both directions in one query, so the caller sees the whole of the table's relationships
    /// without having to know which side of each it is on.
    /// </summary>
    private const string ForeignKeySql = """
        SELECT
            fk.name                                            AS ForeignKeyName,
            ps.name + '.' + pt.name                            AS ParentTable,
            pc.name                                            AS ParentColumn,
            rs.name + '.' + rt.name                            AS ReferencedTable,
            rc.name                                            AS ReferencedColumn,
            fkc.constraint_column_id                           AS Ordinal
        FROM sys.foreign_keys fk
            INNER JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            INNER JOIN sys.tables   pt ON pt.object_id = fk.parent_object_id
            INNER JOIN sys.schemas  ps ON ps.schema_id = pt.schema_id
            INNER JOIN sys.columns  pc ON pc.object_id = fkc.parent_object_id
                                       AND pc.column_id = fkc.parent_column_id
            INNER JOIN sys.tables   rt ON rt.object_id = fk.referenced_object_id
            INNER JOIN sys.schemas  rs ON rs.schema_id = rt.schema_id
            INNER JOIN sys.columns  rc ON rc.object_id = fkc.referenced_object_id
                                       AND rc.column_id = fkc.referenced_column_id
        WHERE fk.parent_object_id = OBJECT_ID(@table)
           OR fk.referenced_object_id = OBJECT_ID(@table)
        ORDER BY fk.name, fkc.constraint_column_id
        """;

    public async Task<DbTableSchema?> DescribeTableSchemaAsync(string tableName, CancellationToken ct)
    {
        var columns = ImmutableArray.CreateBuilder<DbColumnSchema>();
        string schema = "dbo";
        string name = Unqualify(tableName);

        await foreach (var row in ReadAsync(ColumnSql, tableName, ct).ConfigureAwait(false))
        {
            schema = row.GetString(row.GetOrdinal("SchemaName"));
            name = row.GetString(row.GetOrdinal("TableName"));

            string typeName = row.GetString(row.GetOrdinal("TypeName"));
            short maxLength = row.GetInt16(row.GetOrdinal("MaxLength"));

            columns.Add(new DbColumnSchema(
                Name: row.GetString(row.GetOrdinal("ColumnName")),
                SqlType: typeName,
                IsNullable: row.GetBoolean(row.GetOrdinal("IsNullable")),
                IsPrimaryKey: row.GetInt32(row.GetOrdinal("IsPrimaryKey")) == 1,
                IsIdentity: row.GetBoolean(row.GetOrdinal("IsIdentity")),
                IsComputed: row.GetBoolean(row.GetOrdinal("IsComputed")),

                // A row version is a type rather than a flag, and it is the one column SqlMetal marks
                // `IsVersion` — which is what makes LINQ to SQL's optimistic concurrency work at all.
                IsRowVersion: typeName.Equals("timestamp", StringComparison.OrdinalIgnoreCase)
                              || typeName.Equals("rowversion", StringComparison.OrdinalIgnoreCase),

                Ordinal: row.GetInt32(row.GetOrdinal("Ordinal")),
                MaxLength: CharacterLength(typeName, maxLength),
                Precision: row.GetByte(row.GetOrdinal("Precision")),
                Scale: row.GetByte(row.GetOrdinal("Scale"))));
        }

        return columns.Count == 0 ? null : new DbTableSchema(schema, name, columns.ToImmutable());
    }

    public async Task<IReadOnlyList<DbForeignKey>> ForeignKeysAsync(
        string tableName, CancellationToken ct)
    {
        // Keyed on the constraint, because a composite key arrives as one row per column and the
        // pairing between the two sides is the constraint's own column order.
        var byName = new Dictionary<string, (string Parent, List<string> ParentColumns,
            string Referenced, List<string> ReferencedColumns)>(StringComparer.Ordinal);
        var order = new List<string>();

        await foreach (var row in ReadAsync(ForeignKeySql, tableName, ct).ConfigureAwait(false))
        {
            string key = row.GetString(row.GetOrdinal("ForeignKeyName"));

            if (!byName.TryGetValue(key, out var entry))
            {
                entry = (row.GetString(row.GetOrdinal("ParentTable")), [],
                         row.GetString(row.GetOrdinal("ReferencedTable")), []);
                byName[key] = entry;
                order.Add(key);
            }

            entry.ParentColumns.Add(row.GetString(row.GetOrdinal("ParentColumn")));
            entry.ReferencedColumns.Add(row.GetString(row.GetOrdinal("ReferencedColumn")));
        }

        return
        [
            .. order.Select(key =>
            {
                var entry = byName[key];
                return new DbForeignKey(
                    key, entry.Parent, [.. entry.ParentColumns],
                    entry.Referenced, [.. entry.ReferencedColumns]);
            }),
        ];
    }

    public async Task<IReadOnlyList<DbSchemaObject>> ListSchemaObjectsAsync(CancellationToken ct)
    {
        var objects = new List<DbSchemaObject>();

        await foreach (var row in ReadAsync(ObjectListSql, string.Empty, ct).ConfigureAwait(false))
        {
            objects.Add(new DbSchemaObject(
                row.GetString(row.GetOrdinal("SchemaName")),
                row.GetString(row.GetOrdinal("ObjectName")),
                Kind(row.GetString(row.GetOrdinal("ObjectType")))));
        }

        return objects;
    }

    public async Task<DbFunctionSchema?> DescribeFunctionAsync(
        string functionName, CancellationToken ct)
    {
        string? schema = null, name = null;
        var kind = DbSchemaObjectKind.StoredProcedure;

        await foreach (var row in ReadAsync(RoutineSql, functionName, ct).ConfigureAwait(false))
        {
            schema = row.GetString(row.GetOrdinal("SchemaName"));
            name = row.GetString(row.GetOrdinal("ObjectName"));
            kind = Kind(row.GetString(row.GetOrdinal("ObjectType")));
        }

        if (schema is null || name is null)
            return null;

        var parameters = ImmutableArray.CreateBuilder<DbParameterSchema>();
        DbParameterSchema? returnValue = null;

        await foreach (var row in ReadAsync(ParameterSql, functionName, ct).ConfigureAwait(false))
        {
            string typeName = row.GetString(row.GetOrdinal("TypeName"));
            short maxLength = row.GetInt16(row.GetOrdinal("MaxLength"));

            var parameter = new DbParameterSchema(
                Name: row.GetString(row.GetOrdinal("ParameterName")).TrimStart('@'),
                SqlType: typeName,
                IsOutput: row.GetBoolean(row.GetOrdinal("IsOutput")),
                MaxLength: CharacterLength(typeName, maxLength),
                Precision: row.GetByte(row.GetOrdinal("Precision")),
                Scale: row.GetByte(row.GetOrdinal("Scale")));

            // Parameter zero is the catalogue's spelling of a scalar function's return value.
            if (row.GetInt32(row.GetOrdinal("Ordinal")) == 0)
                returnValue = parameter;
            else
                parameters.Add(parameter);
        }

        (ImmutableArray<DbColumnSchema> Columns, string? Note) result = kind switch
        {
            // A table-valued function's rows are ordinary sys.columns rows on the object itself.
            DbSchemaObjectKind.TableFunction =>
                ((await DescribeTableSchemaAsync(functionName, ct).ConfigureAwait(false))
                    ?.Columns ?? [], null),

            DbSchemaObjectKind.StoredProcedure =>
                await DescribeResultSetAsync(functionName, ct).ConfigureAwait(false),

            _ => ([], null),
        };

        return new DbFunctionSchema(
            schema, name, kind, parameters.ToImmutable(), returnValue,
            result.Columns, result.Note);
    }

    /// <summary>
    /// A procedure's result shape, or an empty shape and the reason.
    /// </summary>
    /// <remarks>
    /// <c>sp_describe_first_result_set</c> analyses statically and refuses a procedure built on
    /// dynamic SQL or a temp table. That is an answer rather than a failure — the procedure is still
    /// addable, its element just cannot carry columns — so the error becomes a note for the user
    /// instead of an exception for the caller.
    /// </remarks>
    private async Task<(ImmutableArray<DbColumnSchema> Columns, string? Note)> DescribeResultSetAsync(
        string procedureName, CancellationToken ct)
    {
        var columns = ImmutableArray.CreateBuilder<DbColumnSchema>();
        string? note = null;

        try
        {
            await foreach (var row in ReadAsync(ResultSetSql, procedureName, ct).ConfigureAwait(false))
            {
                if (row.GetBoolean(row.GetOrdinal("is_hidden")))
                    continue;

                int ordinal = row.GetInt32(row.GetOrdinal("column_ordinal"));

                // An unnamed column cannot be a member. Naming it here would generate a property the
                // procedure's author never wrote; saying so lets them alias it instead.
                if (row.IsDBNull(row.GetOrdinal("name"))
                    || row.GetString(row.GetOrdinal("name")) is not { Length: > 0 } columnName)
                {
                    note = $"Column {ordinal} of {procedureName} has no name and was skipped.";
                    continue;
                }

                // "nvarchar(50)" — the bare type is the half the type map wants; length, precision
                // and scale arrive in their own fields.
                string typeName = row.GetString(row.GetOrdinal("system_type_name"));
                int parenthesis = typeName.IndexOf('(');
                if (parenthesis > 0)
                    typeName = typeName[..parenthesis];

                columns.Add(new DbColumnSchema(
                    Name: columnName,
                    SqlType: typeName,
                    IsNullable: row.GetBoolean(row.GetOrdinal("is_nullable")),
                    IsPrimaryKey: false,
                    IsIdentity: row.GetBoolean(row.GetOrdinal("is_identity_column")),
                    IsComputed: false,
                    IsRowVersion: typeName.Equals("timestamp", StringComparison.OrdinalIgnoreCase)
                                  || typeName.Equals("rowversion", StringComparison.OrdinalIgnoreCase),
                    Ordinal: ordinal,
                    MaxLength: CharacterLength(typeName, row.GetInt16(row.GetOrdinal("max_length"))),
                    Precision: row.GetByte(row.GetOrdinal("precision")),
                    Scale: row.GetByte(row.GetOrdinal("scale"))));
            }
        }
        catch (DbException ex)
        {
            return ([], $"The result shape of {procedureName} could not be determined: {ex.Message}");
        }

        return (columns.ToImmutable(), note);
    }

    private static DbSchemaObjectKind Kind(string objectType) => objectType.TrimEnd() switch
    {
        "U" => DbSchemaObjectKind.Table,
        "V" => DbSchemaObjectKind.View,
        "FN" => DbSchemaObjectKind.ScalarFunction,
        "IF" or "TF" => DbSchemaObjectKind.TableFunction,
        _ => DbSchemaObjectKind.StoredProcedure,
    };

    /// <summary>
    /// The length in the unit the type is measured in, or nothing where length says nothing.
    /// </summary>
    /// <remarks>
    /// <c>sys.columns.max_length</c> is bytes always, so an <c>nvarchar(50)</c> reads 100 and has to
    /// be halved to be written back as <c>NVarChar(50)</c>. <c>-1</c> is <c>max</c> and is passed
    /// through unchanged for the type map to spell.
    /// </remarks>
    private static int? CharacterLength(string typeName, short maxLength) => typeName.ToLowerInvariant() switch
    {
        "nvarchar" or "nchar" => maxLength == -1 ? -1 : maxLength / 2,
        "varchar" or "char" or "varbinary" or "binary" => maxLength,
        _ => null,
    };

    /// <summary>The table name without its schema, for the fallback when the table does not exist.</summary>
    private static string Unqualify(string tableName) =>
        tableName.LastIndexOf('.') is var dot && dot >= 0 ? tableName[(dot + 1)..] : tableName;

    private async IAsyncEnumerable<DbDataReader> ReadAsync(
        string sql, string tableName,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = CreateCommand(sql, connection);
        BindParameters(command, new Dictionary<string, object?> { ["@table"] = tableName });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            yield return reader;
    }
}

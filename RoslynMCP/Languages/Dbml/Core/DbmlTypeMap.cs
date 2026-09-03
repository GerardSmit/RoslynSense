using System.Globalization;
using RoslynMCP.Services.Database;

namespace RoslynMCP.Languages.Dbml.Core;

/// <summary>
/// A SQL Server column as a <c>.dbml</c> spells it: the <c>Type</c> attribute's CLR name and the
/// <c>DbType</c> attribute's provider string.
/// </summary>
/// <remarks>
/// <para>
/// Written to match what SqlMetal emits rather than to be merely correct, because the refresh
/// compares its output against elements SqlMetal wrote. A map that spelled <c>NVarChar(50) NOT NULL</c>
/// as <c>nvarchar(50) not null</c> would be a true description of the same column and would make
/// every refresh report every column as changed.
/// </para>
/// <para>
/// Pure and bounded — no database, no connection, no I/O — so the whole of the mapping is testable
/// against a table of expectations, which is the half of a refresh most likely to be wrong in a way
/// nobody notices until a designer stops compiling.
/// </para>
/// </remarks>
internal static class DbmlTypeMap
{
    /// <summary>
    /// The CLR type SqlMetal declares the generated property as.
    /// </summary>
    /// <remarks>
    /// Always the non-nullable name. LINQ to SQL carries nullability in <c>CanBeNull</c> and makes the
    /// property a <c>Nullable&lt;T&gt;</c> from that, so writing <c>System.Int32?</c> into
    /// <c>Type</c> would be saying it twice and would not match anything SqlMetal produced.
    /// </remarks>
    public static string ClrTypeFor(string sqlType) => sqlType.ToLowerInvariant() switch
    {
        "bit" => "System.Boolean",
        "tinyint" => "System.Byte",
        "smallint" => "System.Int16",
        "int" => "System.Int32",
        "bigint" => "System.Int64",

        "decimal" or "numeric" or "money" or "smallmoney" => "System.Decimal",
        "float" => "System.Double",
        "real" => "System.Single",

        "char" or "nchar" or "varchar" or "nvarchar" or "text" or "ntext" or "xml"
            or "sysname" => "System.String",

        "date" or "datetime" or "datetime2" or "smalldatetime" => "System.DateTime",
        "datetimeoffset" => "System.DateTimeOffset",
        "time" => "System.TimeSpan",

        "uniqueidentifier" => "System.Guid",

        // A row version is bytes to the CLR, and SqlMetal declares it as such rather than as the
        // Binary struct — the struct is what the older designer emitted and is not what round-trips.
        "binary" or "varbinary" or "image" or "timestamp" or "rowversion" => "System.Data.Linq.Binary",

        // Anything unrecognised, including a UDT: object is what SqlMetal falls back to, and it at
        // least compiles.
        _ => "System.Object",
    };

    /// <summary>
    /// The <c>DbType</c> string — the type as the database has it, plus the qualifiers LINQ to SQL
    /// needs at runtime to round-trip a value.
    /// </summary>
    /// <remarks>
    /// The three qualifiers are not decoration. <c>NOT NULL</c> is what makes an insert of a default
    /// value fail loudly rather than silently; <c>IDENTITY</c> is what makes the context read the key
    /// back after an insert instead of writing zero; and the length is what stops a parameter being
    /// sized from the value, which is what turns a plan cache into a plan per string length.
    /// </remarks>
    public static string DbTypeFor(DbColumnSchema column)
    {
        var text = new System.Text.StringBuilder(Cased(column.SqlType));

        if (Length(column) is { Length: > 0 } length)
            text.Append('(').Append(length).Append(')');

        if (!column.IsNullable)
            text.Append(" NOT NULL");

        if (column.IsIdentity)
            text.Append(" IDENTITY");

        return text.ToString();
    }

    /// <summary>
    /// Whether the database fills the column in, which is what <c>IsDbGenerated</c> means to LINQ to
    /// SQL: it must not be sent on an insert and must be read back afterwards.
    /// </summary>
    /// <remarks>
    /// Three separate things arrive at the same answer — an identity, a computed column and a row
    /// version — and getting any of them wrong produces the same failure: an insert that sends a value
    /// for a column the server owns, which SQL Server rejects.
    /// </remarks>
    public static bool IsDbGenerated(DbColumnSchema column) =>
        column.IsIdentity || column.IsComputed || column.IsRowVersion;

    /// <summary>
    /// The parenthesised part of a <c>DbType</c>, empty where the type has no size to state.
    /// </summary>
    /// <remarks>
    /// The defaults are left off deliberately. SqlMetal writes <c>Decimal(18,0)</c> but plain
    /// <c>DateTime</c> and plain <c>NVarChar(MAX)</c>, and a length written where SqlMetal writes none
    /// reads as a change on every refresh of a table nobody touched.
    /// </remarks>
    private static string Length(DbColumnSchema column)
    {
        switch (column.SqlType.ToLowerInvariant())
        {
            case "char" or "nchar" or "varchar" or "nvarchar" or "binary" or "varbinary":
                return column.MaxLength switch
                {
                    null => string.Empty,
                    -1 => "MAX",
                    var max => max.Value.ToString(CultureInfo.InvariantCulture),
                };

            case "decimal" or "numeric":
                return $"{column.Precision ?? 18},{column.Scale ?? 0}";

            // A fractional-seconds type states its scale and nothing else, and only when it is not
            // the default of 7 — which is the one SqlMetal leaves off.
            case "datetime2" or "time" or "datetimeoffset":
                return column.Scale is { } scale && scale != 7
                    ? scale.ToString(CultureInfo.InvariantCulture)
                    : string.Empty;

            default:
                return string.Empty;
        }
    }

    /// <summary>
    /// The type name in SqlMetal's casing, which is the .NET <c>SqlDbType</c> member's rather than
    /// the catalogue's.
    /// </summary>
    private static string Cased(string sqlType) => sqlType.ToLowerInvariant() switch
    {
        "bigint" => "BigInt",
        "binary" => "Binary",
        "bit" => "Bit",
        "char" => "Char",
        "date" => "Date",
        "datetime" => "DateTime",
        "datetime2" => "DateTime2",
        "datetimeoffset" => "DateTimeOffset",
        "decimal" => "Decimal",
        "float" => "Float",
        "image" => "Image",
        "int" => "Int",
        "money" => "Money",
        "nchar" => "NChar",
        "ntext" => "NText",
        "numeric" => "Decimal",
        "nvarchar" => "NVarChar",
        "real" => "Real",
        "rowversion" or "timestamp" => "rowversion",
        "smalldatetime" => "SmallDateTime",
        "smallint" => "SmallInt",
        "smallmoney" => "SmallMoney",
        "sysname" => "NVarChar",
        "text" => "Text",
        "time" => "Time",
        "tinyint" => "TinyInt",
        "uniqueidentifier" => "UniqueIdentifier",
        "varbinary" => "VarBinary",
        "varchar" => "VarChar",
        "xml" => "Xml",

        // A user-defined type is named by the database and there is nothing to case it to.
        _ => sqlType,
    };
}

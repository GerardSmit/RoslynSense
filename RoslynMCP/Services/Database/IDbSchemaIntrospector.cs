using System.Collections.Immutable;

namespace RoslynMCP.Services.Database;

/// <summary>
/// A table's shape, in the terms a code generator needs rather than the terms a reader does.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="IDbProvider"/> on purpose. <c>DescribeTableAsync</c> answers with
/// <c>string[]</c> rows because it exists to be printed — it carries no primary key, no identity and
/// no foreign keys, and widening it to carry them would push three concepts every provider would then
/// have to answer for onto providers that have no caller for them. A provider that does not implement
/// this declines cleanly, which is the right answer for the two that cannot be reached from a
/// <c>.dbml</c> anyway: LINQ to SQL is SQL Server only.
/// </para>
/// <para>
/// Everything here is what a <c>&lt;Column&gt;</c> or an <c>&lt;Association&gt;</c> element needs to
/// be written from nothing. Nullability, identity, computedness and row-version each change an
/// attribute SqlMetal emits, and each is invisible in <c>INFORMATION_SCHEMA</c>'s answer.
/// </para>
/// </remarks>
public interface IDbSchemaIntrospector
{
    /// <summary>The table's columns, or <c>null</c> when the database has no such table.</summary>
    /// <remarks>Views answer too: LINQ to SQL maps a view as a <c>&lt;Table&gt;</c>, so anything a
    /// <c>.dbml</c> spells that way has to be describable the same way.</remarks>
    Task<DbTableSchema?> DescribeTableSchemaAsync(string tableName, CancellationToken ct);

    /// <summary>
    /// Every table, view, function and procedure the database offers, names only.
    /// </summary>
    /// <remarks>
    /// Names only on purpose: this backs a picker over a database that may hold a thousand objects,
    /// and describing each of them before anyone has picked one would turn opening the picker into a
    /// full catalogue crawl.
    /// </remarks>
    Task<IReadOnlyList<DbSchemaObject>> ListSchemaObjectsAsync(CancellationToken ct);

    /// <summary>
    /// A function or procedure in the shape a <c>&lt;Function&gt;</c> element needs, or <c>null</c>
    /// when the database has no such routine.
    /// </summary>
    Task<DbFunctionSchema?> DescribeFunctionAsync(string functionName, CancellationToken ct);

    /// <summary>
    /// Every foreign key the table takes part in, as a child <em>and</em> as a parent.
    /// </summary>
    /// <remarks>
    /// Both directions, because an association is a pair: refreshing <c>Orders</c> has to produce the
    /// <c>Customer</c> property on the order as well as the <c>Orders</c> collection on the customer,
    /// and only one of those is a key the table itself holds.
    /// </remarks>
    Task<IReadOnlyList<DbForeignKey>> ForeignKeysAsync(string tableName, CancellationToken ct);
}

/// <summary>One column, as the catalogue has it.</summary>
/// <param name="SqlType">The type's own name — <c>nvarchar</c>, <c>int</c> — without length or
/// nullability, which are separate fields here and are recombined by the type map.</param>
/// <param name="MaxLength">Characters for a string type and bytes for a binary one, with <c>-1</c>
/// meaning <c>max</c>; <c>null</c> for a type where length means nothing.</param>
public sealed record DbColumnSchema(
    string Name,
    string SqlType,
    bool IsNullable,
    bool IsPrimaryKey,
    bool IsIdentity,
    bool IsComputed,
    bool IsRowVersion,
    int Ordinal,
    int? MaxLength,
    byte? Precision,
    byte? Scale);

/// <summary>A table and its columns, in ordinal order.</summary>
/// <param name="Schema">The owning schema — <c>dbo</c> — which the model spells out in
/// <c>Name="dbo.Orders"</c> and a caller may not have supplied.</param>
public sealed record DbTableSchema(
    string Schema,
    string Name,
    ImmutableArray<DbColumnSchema> Columns)
{
    /// <summary>The name as a <c>.dbml</c> writes it.</summary>
    public string QualifiedName => $"{Schema}.{Name}";
}

/// <summary>What a catalogue object is, in the kinds a <c>.dbml</c> can model.</summary>
public enum DbSchemaObjectKind
{
    Table,
    View,
    ScalarFunction,
    TableFunction,
    StoredProcedure,
}

/// <summary>One object the catalogue offers, by name.</summary>
public sealed record DbSchemaObject(string Schema, string Name, DbSchemaObjectKind Kind)
{
    /// <summary>The name as a <c>.dbml</c> writes it.</summary>
    public string QualifiedName => $"{Schema}.{Name}";
}

/// <summary>One parameter of a routine, or its return value when <paramref name="Name"/> is empty.</summary>
/// <param name="Name">The parameter's name without its <c>@</c>, which is how a <c>.dbml</c> spells
/// it; empty for the return value, which SQL Server's catalogue models as parameter zero.</param>
public sealed record DbParameterSchema(
    string Name,
    string SqlType,
    bool IsOutput,
    int? MaxLength,
    byte? Precision,
    byte? Scale);

/// <summary>
/// A routine in the terms a <c>&lt;Function&gt;</c> element is written in.
/// </summary>
/// <param name="ReturnValue">The scalar a function returns, or <c>null</c> for a routine whose
/// answer is rows — or a procedure, whose implicit return is an <c>int</c> the element states
/// without the catalogue's help.</param>
/// <param name="ResultColumns">The rows a table-valued function or a procedure produces; empty for
/// a scalar function, and empty with a <paramref name="Note"/> for a procedure whose result shape
/// the server could not determine.</param>
public sealed record DbFunctionSchema(
    string Schema,
    string Name,
    DbSchemaObjectKind Kind,
    ImmutableArray<DbParameterSchema> Parameters,
    DbParameterSchema? ReturnValue,
    ImmutableArray<DbColumnSchema> ResultColumns,
    string? Note = null)
{
    /// <summary>The name as a <c>.dbml</c> writes it.</summary>
    public string QualifiedName => $"{Schema}.{Name}";
}

/// <summary>
/// One foreign key, from the child that holds it to the parent it points at.
/// </summary>
/// <param name="ParentColumns">The child table's own columns — SQL Server's catalogue calls the
/// key-holding side the parent, and this keeps its word for it rather than inventing a third.</param>
public sealed record DbForeignKey(
    string Name,
    string ParentTable,
    ImmutableArray<string> ParentColumns,
    string ReferencedTable,
    ImmutableArray<string> ReferencedColumns);

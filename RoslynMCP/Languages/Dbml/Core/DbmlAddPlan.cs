using System.Collections.Immutable;
using RoslynMCP.Services.Database;

namespace RoslynMCP.Languages.Dbml.Core;

/// <summary>A <c>&lt;Table&gt;</c> the model does not have yet, ready to be written whole.</summary>
/// <param name="Name">The database's qualified name — <c>dbo.Orders</c> — which is what the
/// <c>Name</c> attribute carries.</param>
/// <param name="TypeName">The entity class, unique across the file's types because SqlMetal
/// generates one class per <c>&lt;Type&gt;</c> and two claiming one name is a designer that does not
/// compile.</param>
internal sealed record DbmlTableDraft(
    string Name,
    string Member,
    string TypeName,
    ImmutableArray<DbmlColumnDraft> Columns);

/// <summary>One <c>&lt;Parameter&gt;</c> of a function being added.</summary>
/// <param name="Direction">The <c>Direction</c> attribute — <c>InOut</c> for an output parameter —
/// or <c>null</c> for the default SqlMetal leaves unwritten.</param>
internal sealed record DbmlParameterDraft(
    string Name,
    string ClrType,
    string DbType,
    string? Direction);

/// <summary>A <c>&lt;Function&gt;</c> the model does not have yet.</summary>
/// <param name="ReturnClrType">The <c>&lt;Return&gt;</c> element's type, or <c>null</c> when the
/// routine's answer is rows.</param>
/// <param name="ElementTypeName">The generated result class — <c>GetOrdersResult</c> — or
/// <c>null</c> when there is no result shape to name.</param>
internal sealed record DbmlFunctionDraft(
    string Name,
    string Method,
    bool IsComposable,
    ImmutableArray<DbmlParameterDraft> Parameters,
    string? ReturnClrType,
    string? ReturnDbType,
    string? ElementTypeName,
    ImmutableArray<DbmlColumnDraft> ElementColumns);

/// <summary>
/// What adding database objects to a <c>.dbml</c> would write.
/// </summary>
/// <remarks>
/// Pure, like <see cref="DbmlRefreshPlanner"/> and for the same reason: everything here takes a
/// parsed model and described catalogue objects, so the interesting cases — a name already taken, a
/// table modelled without its schema, a procedure with no describable result — are testable without
/// arranging a server.
/// </remarks>
internal static class DbmlAddPlanner
{
    /// <summary>
    /// The catalogue objects the model does not have yet, in the catalogue's order.
    /// </summary>
    /// <remarks>
    /// A model commonly writes <c>Name="Orders"</c> where the database says <c>dbo.Orders</c>, so an
    /// unqualified model name counts as the <c>dbo</c> object of that name — the default schema is
    /// the one SqlMetal itself omits. An unqualified name is never matched against another schema's
    /// object: <c>audit.Orders</c> being offered next to a modelled <c>Orders</c> is correct, because
    /// they are different tables.
    /// </remarks>
    public static ImmutableArray<DbSchemaObject> Missing(
        DbmlDatabase database, IEnumerable<DbSchemaObject> objects)
    {
        return
        [
            .. objects.Where(o => o.Kind is DbSchemaObjectKind.Table or DbSchemaObjectKind.View
                ? !database.Tables.Any(t => Names(t.Name, o))
                : !database.Functions.Any(f => Names(f.Name, o))),
        ];

        static bool Names(string modelName, DbSchemaObject candidate) =>
            string.Equals(modelName, candidate.QualifiedName, StringComparison.OrdinalIgnoreCase)
            || (!modelName.Contains('.')
                && string.Equals(candidate.Schema, "dbo", StringComparison.OrdinalIgnoreCase)
                && string.Equals(modelName, candidate.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Table elements for every described schema, named without colliding with the model or each
    /// other.
    /// </summary>
    /// <remarks>
    /// The member is the bare name pluralised, which is what the Visual Studio designer produces and
    /// therefore what the rest of such a solution already reads like. A name that is already plural
    /// is left alone rather than pluralised again — the designer's inflector knows <c>Orders</c> is
    /// plural, and <c>Orderses</c> is the bug a naive rule would write.
    /// </remarks>
    public static ImmutableArray<DbmlTableDraft> PlanTables(
        IEnumerable<DbTableSchema> schemas, DbmlDatabase database)
    {
        var claimedTypes = ClaimedTypes(database);
        var claimedMembers = new HashSet<string>(
            database.Tables.Select(t => t.Member), StringComparer.Ordinal);

        return
        [
            .. schemas.Select(schema =>
            {
                string typeName = Claim(Identifier(schema.Name), claimedTypes);
                string member = Claim(Pluralize(Identifier(schema.Name)), claimedMembers);

                return new DbmlTableDraft(
                    schema.QualifiedName,
                    member,
                    typeName,
                    [.. schema.Columns.Select(DbmlRefreshPlanner.Draft)]);
            }),
        ];
    }

    /// <summary>
    /// Function elements for every described routine.
    /// </summary>
    /// <remarks>
    /// A procedure with no describable result set still gets its element, carrying the
    /// <c>Int32</c> return LINQ to SQL gives every procedure — the method is callable, it just hands
    /// back a return code instead of rows, and the introspector's note says why.
    /// </remarks>
    public static ImmutableArray<DbmlFunctionDraft> PlanFunctions(
        IEnumerable<DbFunctionSchema> schemas, DbmlDatabase database)
    {
        var claimedTypes = ClaimedTypes(database);
        var claimedMethods = new HashSet<string>(
            database.Functions.Select(f => f.Member), StringComparer.Ordinal);

        return
        [
            .. schemas.Select(schema =>
            {
                string method = Claim(Identifier(schema.Name), claimedMethods);

                string? elementType = schema.ResultColumns.IsEmpty
                    ? null
                    : Claim($"{method}Result", claimedTypes);

                // Rows, a declared scalar, or a procedure's implicit return code — one of the three,
                // because a <Function> with neither <Return> nor <ElementType> is a method SqlMetal
                // cannot give a return type to.
                var returnValue = elementType is null
                    ? schema.ReturnValue ?? new DbParameterSchema(
                        string.Empty, "int", IsOutput: false, null, null, null)
                    : null;

                return new DbmlFunctionDraft(
                    schema.QualifiedName,
                    method,
                    IsComposable: schema.Kind
                        is DbSchemaObjectKind.ScalarFunction or DbSchemaObjectKind.TableFunction,
                    [.. schema.Parameters.Select(Parameter)],
                    ReturnClrType: returnValue is null ? null : DbmlTypeMap.ClrTypeFor(returnValue.SqlType),
                    ReturnDbType: returnValue is null ? null : DbType(returnValue),
                    elementType,
                    [.. schema.ResultColumns.Select(DbmlRefreshPlanner.Draft)]);
            }),
        ];
    }

    private static DbmlParameterDraft Parameter(DbParameterSchema parameter) => new(
        parameter.Name,
        DbmlTypeMap.ClrTypeFor(parameter.SqlType),
        DbType(parameter),
        // InOut rather than Out: a T-SQL OUTPUT parameter is always readable too, and InOut is what
        // SqlMetal writes for one.
        Direction: parameter.IsOutput ? "InOut" : null);

    /// <summary>The <c>DbType</c> of a parameter, which never carries table qualifiers.</summary>
    private static string DbType(DbParameterSchema parameter) =>
        DbmlTypeMap.DbTypeFor(new DbColumnSchema(
            parameter.Name, parameter.SqlType,
            IsNullable: true, IsPrimaryKey: false, IsIdentity: false, IsComputed: false,
            IsRowVersion: false, Ordinal: 0,
            parameter.MaxLength, parameter.Precision, parameter.Scale));

    /// <summary>Every class name the file already generates, entity and result types alike.</summary>
    private static HashSet<string> ClaimedTypes(DbmlDatabase database) =>
        new(database.AllTypes().Select(t => t.Name), StringComparer.Ordinal);

    /// <summary>The preferred name, or the first numbered variant of it that is free.</summary>
    private static string Claim(string preferred, HashSet<string> claimed)
    {
        string name = preferred;

        for (int suffix = 1; !claimed.Add(name) && suffix < 100; suffix++)
            name = $"{preferred}{suffix}";

        return name;
    }

    /// <summary>
    /// The name as a C# identifier: anything else becomes an underscore, the way SqlMetal maps a
    /// name it cannot use.
    /// </summary>
    internal static string Identifier(string name)
    {
        if (name.Length == 0)
            return "_";

        var text = new System.Text.StringBuilder(name.Length);

        foreach (char c in name)
            text.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');

        if (char.IsDigit(text[0]))
            text.Insert(0, '_');

        return text.ToString();
    }

    /// <summary>
    /// The member a <c>Table&lt;T&gt;</c> property gets: plural, unless the table's own name already
    /// is.
    /// </summary>
    internal static string Pluralize(string name)
    {
        if (name.Length == 0 || name.EndsWith('s') || name.EndsWith('S'))
            return name;

        if ((name.EndsWith('y') || name.EndsWith('Y'))
            && name.Length > 1 && !IsVowel(name[^2]))
        {
            return name[..^1] + (name.EndsWith('Y') ? "IES" : "ies");
        }

        if (name.EndsWith("x", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("z", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("ch", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("sh", StringComparison.OrdinalIgnoreCase))
        {
            return name + "es";
        }

        return name + "s";
    }

    private static bool IsVowel(char c) => "aeiouAEIOU".Contains(c);
}

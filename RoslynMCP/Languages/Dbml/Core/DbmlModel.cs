using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMCP.Languages.Dbml.Core;

/// <summary>What a declaration in a <c>.dbml</c> is, for the features that treat them uniformly.</summary>
internal enum DbmlDeclarationKind
{
    Database,
    Table,
    Type,
    Column,
    Association,
    Function,
}

/// <summary>
/// The shape every declaration in a <c>.dbml</c> shares, so the outline, the resolver and the code
/// lens can walk one flat list instead of five typed ones.
/// </summary>
internal interface IDbmlDeclaration
{
    DbmlDeclarationKind Kind { get; }

    /// <summary>The database's own name for the thing — <c>dbo.Products</c>, <c>Id</c>, <c>FK_…</c>.</summary>
    string Name { get; }

    /// <summary>
    /// The C# member SqlMetal names after it, which is the <c>Member</c> attribute where the model
    /// carries one and <see cref="Name"/> where it does not — the same default SqlMetal applies.
    /// </summary>
    string Member { get; }

    /// <summary>
    /// The declaration's identity, unique within the file and stable across a reparse.
    /// </summary>
    /// <remarks>
    /// A string rather than the node, for the reason <c>ProtoDeclarationRef</c> gives: the binder
    /// reads the file on disk while the caller is usually looking at an editor buffer, so handing
    /// back node instances would give the caller spans measured against text it is not showing.
    /// </remarks>
    string Key { get; }

    /// <summary>The whole element — a document symbol's range.</summary>
    TextSpan Span { get; }

    /// <summary>
    /// The characters a jump should land on and a lens should sit above: the <c>Member</c> attribute's
    /// value where there is one, else <c>Name</c>'s, else the tag name.
    /// </summary>
    TextSpan SelectionSpan { get; }
}

/// <summary>The root <c>&lt;Database&gt;</c> element, and everything under it.</summary>
/// <param name="Class">The <c>DataContext</c> class SqlMetal generates, which the model names
/// explicitly and which defaults to <see cref="IDbmlDeclaration.Name"/> when it does not.</param>
internal sealed record DbmlDatabase(
    string Name,
    string Class,
    string? ContextNamespace,
    string? EntityNamespace,
    TextSpan Span,
    TextSpan SelectionSpan,
    ImmutableArray<DbmlTable> Tables,
    ImmutableArray<DbmlFunction> Functions) : IDbmlDeclaration
{
    public static readonly DbmlDatabase Empty = new(
        "", "", null, null, default, default, [], []);

    public DbmlDeclarationKind Kind => DbmlDeclarationKind.Database;

    public string Member => Class;

    public string Key => "database";

    /// <summary>Whether the parse found a <c>&lt;Database&gt;</c> element at all.</summary>
    public bool IsEmpty => Span.IsEmpty && Tables.IsEmpty && Functions.IsEmpty;

    /// <summary>
    /// Every declaration in the file, in document order, flattened.
    /// </summary>
    /// <remarks>
    /// The root is included: it earns a lens of its own — uses of the generated
    /// <c>DataContext</c> — and leaving it out of the walk would mean the one feature that wants it
    /// re-walking the tree.
    /// </remarks>
    public IEnumerable<IDbmlDeclaration> AllDeclarations()
    {
        yield return this;

        foreach (var table in Tables)
        {
            yield return table;

            foreach (var type in table.AllTypes())
            {
                yield return type;

                foreach (var column in type.Columns)
                    yield return column;

                foreach (var association in type.Associations)
                    yield return association;
            }
        }

        foreach (var function in Functions)
            yield return function;
    }

    /// <summary>Every <c>&lt;Type&gt;</c> in the file, inherited ones included.</summary>
    public IEnumerable<DbmlType> AllTypes() => Tables.SelectMany(table => table.AllTypes());

    public DbmlTable? TableNamed(string name) =>
        Tables.FirstOrDefault(table => string.Equals(table.Name, name, StringComparison.OrdinalIgnoreCase));

    public IDbmlDeclaration? Find(string key) =>
        AllDeclarations().FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.Ordinal));
}

/// <summary>A <c>&lt;Table&gt;</c>: one database table, and the <c>Table&lt;T&gt;</c> property on the
/// context that exposes it.</summary>
internal sealed record DbmlTable(
    string Name,
    string Member,
    TextSpan Span,
    TextSpan SelectionSpan,
    ImmutableArray<DbmlType> Types) : IDbmlDeclaration
{
    public DbmlDeclarationKind Kind => DbmlDeclarationKind.Table;

    public string Key => $"table:{Name}";

    /// <summary>The row type, which is the first <c>&lt;Type&gt;</c>; the rest are derived from it.</summary>
    public DbmlType? RowType => Types.Length > 0 ? Types[0] : null;

    /// <summary>The row type and everything inheriting from it, depth first.</summary>
    public IEnumerable<DbmlType> AllTypes() => Types.SelectMany(type => type.SelfAndDerived());
}

/// <summary>A <c>&lt;Type&gt;</c>: the entity class SqlMetal generates for a table's rows.</summary>
internal sealed record DbmlType(
    string Name,
    TextSpan Span,
    TextSpan SelectionSpan,
    ImmutableArray<DbmlColumn> Columns,
    ImmutableArray<DbmlAssociation> Associations,
    ImmutableArray<DbmlType> DerivedTypes) : IDbmlDeclaration
{
    public DbmlDeclarationKind Kind => DbmlDeclarationKind.Type;

    public string Member => Name;

    public string Key => $"type:{Name}";

    public IEnumerable<DbmlType> SelfAndDerived()
    {
        yield return this;

        foreach (var derived in DerivedTypes)
        {
            foreach (var type in derived.SelfAndDerived())
                yield return type;
        }
    }

    public DbmlColumn? ColumnNamed(string name) =>
        Columns.FirstOrDefault(column =>
            string.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A <c>&lt;Column&gt;</c>: one database column, and the property generated for it.</summary>
/// <param name="ClrType">The <c>Type</c> attribute — <c>System.Int32</c> — which is what SqlMetal
/// declares the property as.</param>
/// <param name="DbType">The <c>DbType</c> attribute — <c>Int NOT NULL IDENTITY</c> — which is the
/// column as the database has it, and what a refresh compares against.</param>
internal sealed record DbmlColumn(
    string Name,
    string Member,
    string OwnerTypeName,
    string? ClrType,
    string? DbType,
    bool IsPrimaryKey,
    bool IsDbGenerated,
    bool IsVersion,
    bool CanBeNull,
    TextSpan Span,
    TextSpan SelectionSpan) : IDbmlDeclaration
{
    public DbmlDeclarationKind Kind => DbmlDeclarationKind.Column;

    public string Key => $"column:{OwnerTypeName}.{Member}";
}

/// <summary>
/// An <c>&lt;Association&gt;</c>: one end of a foreign-key relationship.
/// </summary>
/// <remarks>
/// One end, not the relationship. LINQ to SQL writes a pair sharing a single <c>Name</c> — the child
/// end carrying <c>IsForeignKey="true"</c> and the parent end the collection — so <c>Name</c> cannot
/// identify either half on its own and <see cref="IDbmlDeclaration.Member"/> is what tells them
/// apart. The generated property name is the member, which is also what binds it to a symbol.
/// </remarks>
internal sealed record DbmlAssociation(
    string Name,
    string Member,
    string OwnerTypeName,
    string ThisKey,
    string OtherKey,
    string TargetTypeName,
    bool IsForeignKey,
    TextSpan Span,
    TextSpan SelectionSpan) : IDbmlDeclaration
{
    public DbmlDeclarationKind Kind => DbmlDeclarationKind.Association;

    public string Key => $"association:{OwnerTypeName}.{Member}";
}

/// <summary>A <c>&lt;Function&gt;</c>: a stored procedure or function, and the context method for it.</summary>
internal sealed record DbmlFunction(
    string Name,
    string Member,
    bool IsComposable,
    TextSpan Span,
    TextSpan SelectionSpan) : IDbmlDeclaration
{
    public DbmlDeclarationKind Kind => DbmlDeclarationKind.Function;

    public string Key => $"function:{Name}";
}

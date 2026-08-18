using System.Collections.Immutable;
using RoslynMCP.Services.Database;

namespace RoslynMCP.Languages.Dbml.Core;

/// <summary>A column as the database has it, in the attributes a <c>&lt;Column&gt;</c> carries.</summary>
internal sealed record DbmlColumnDraft(
    string Name,
    string Member,
    string ClrType,
    string DbType,
    bool IsPrimaryKey,
    bool IsDbGenerated,
    bool IsVersion,
    bool CanBeNull);

/// <summary>One end of a relationship the database has and the model does not.</summary>
/// <param name="OwnerTypeName">The <c>&lt;Type&gt;</c> the element is written into.</param>
internal sealed record DbmlAssociationDraft(
    string OwnerTypeName,
    string Name,
    string Member,
    string ThisKey,
    string OtherKey,
    string TargetTypeName,
    bool IsForeignKey,
    bool IsCollection);

/// <summary>A column the model and the database disagree about.</summary>
/// <param name="Changes">What differs, in words — this is what the confirmation shows, and "DbType
/// Int NOT NULL → Int NULL" is a thing a reader can approve where "Orders.CustomerId changed" is
/// not.</param>
internal sealed record DbmlColumnUpdate(
    DbmlColumn Existing, DbmlColumnDraft Refreshed, ImmutableArray<string> Changes);

/// <summary>
/// What refreshing one <c>&lt;Table&gt;</c> against the live database would do.
/// </summary>
/// <remarks>
/// <para>
/// A value, computed and returned before anything is written. The removals are the reason: dropping
/// a <c>&lt;Column&gt;</c> deletes a property the solution may be full of references to, and the
/// database being the authority on what columns exist does not make it the authority on whether the
/// model is finished being edited. So the plan is shown, the removals are confirmed separately from
/// the rest, and only then does anything reach the file.
/// </para>
/// <para>
/// Pure — the diff takes a parsed table and a described schema and touches nothing else — so the
/// whole of it is testable against a fake introspector, which is where the interesting cases are.
/// </para>
/// </remarks>
internal sealed record DbmlRefreshPlan(
    string TableName,
    string TypeName,
    ImmutableArray<DbmlColumnDraft> Added,
    ImmutableArray<DbmlColumnUpdate> Updated,
    ImmutableArray<DbmlColumn> Removed,
    ImmutableArray<DbmlAssociationDraft> Associations,
    ImmutableArray<string> Notes)
{
    public bool IsEmpty =>
        Added.IsEmpty && Updated.IsEmpty && Removed.IsEmpty && Associations.IsEmpty;

    /// <summary>The plan in one line, for a status bar and a confirmation title.</summary>
    public string Summary
    {
        get
        {
            if (IsEmpty)
                return $"{TableName} is up to date.";

            var parts = new List<string>();

            if (!Added.IsEmpty) parts.Add($"{Added.Length} to add");
            if (!Updated.IsEmpty) parts.Add($"{Updated.Length} to update");
            if (!Removed.IsEmpty) parts.Add($"{Removed.Length} to remove");
            if (!Associations.IsEmpty) parts.Add($"{Associations.Length} association(s)");

            return $"{TableName}: {string.Join(", ", parts)}.";
        }
    }
}

/// <summary>
/// The diff between a <c>&lt;Table&gt;</c> as the model has it and the table as the database has it.
/// </summary>
internal static class DbmlRefreshPlanner
{
    /// <summary>
    /// Plans a refresh of one table.
    /// </summary>
    /// <param name="table">The table as parsed from the file being refreshed.</param>
    /// <param name="schema">The table as the database describes it.</param>
    /// <param name="foreignKeys">Every key the table takes part in, in both directions.</param>
    /// <param name="database">The whole model, because an association names a type that has to
    /// already be in the file for the element to mean anything.</param>
    public static DbmlRefreshPlan Plan(
        DbmlTable table,
        DbTableSchema schema,
        IReadOnlyList<DbForeignKey> foreignKeys,
        DbmlDatabase database)
    {
        var rowType = table.RowType;
        string typeName = rowType?.Name ?? schema.Name;

        // The row type only. A derived type's columns belong to the inheritance mapping rather than
        // to the table, and moving one would silently change which class a column is read from.
        var existing = rowType?.Columns ?? [];

        var added = ImmutableArray.CreateBuilder<DbmlColumnDraft>();
        var updated = ImmutableArray.CreateBuilder<DbmlColumnUpdate>();
        var notes = ImmutableArray.CreateBuilder<string>();
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var column in schema.Columns)
        {
            var draft = Draft(column);

            var current = existing.FirstOrDefault(c =>
                string.Equals(c.Name, column.Name, StringComparison.OrdinalIgnoreCase));

            if (current is null)
            {
                added.Add(draft);
                continue;
            }

            matched.Add(current.Name);

            if (Differences(current, draft) is { Length: > 0 } changes)
                updated.Add(new DbmlColumnUpdate(current, draft, changes));
        }

        // Everything the model has and the database does not. Kept whole rather than as names,
        // because the caller shows them and then decides.
        var removed = existing
            .Where(c => !matched.Contains(c.Name))
            .ToImmutableArray();

        var associations = PlanAssociations(schema, foreignKeys, database, notes);

        return new DbmlRefreshPlan(
            schema.QualifiedName, typeName,
            added.ToImmutable(), updated.ToImmutable(), removed, associations, notes.ToImmutable());
    }

    /// <summary>
    /// A column element written from the catalogue.
    /// </summary>
    /// <remarks>
    /// The member name is the column name unchanged. SqlMetal's own de-duplication — appending a
    /// digit when a column collides with the class name — is not reproduced: it applies to cases this
    /// refresh cannot create, and inventing a member name that differs from the column would break
    /// the binding this whole pack rests on.
    /// </remarks>
    private static DbmlColumnDraft Draft(DbColumnSchema column) => new(
        Name: column.Name,
        Member: column.Name,
        ClrType: DbmlTypeMap.ClrTypeFor(column.SqlType),
        DbType: DbmlTypeMap.DbTypeFor(column),
        IsPrimaryKey: column.IsPrimaryKey,
        IsDbGenerated: DbmlTypeMap.IsDbGenerated(column),
        IsVersion: column.IsRowVersion,
        CanBeNull: column.IsNullable);

    /// <summary>
    /// What the model says about a column that the database does not.
    /// </summary>
    /// <remarks>
    /// <c>Member</c> is deliberately not compared. Renaming the generated property is the one edit a
    /// <c>.dbml</c> is for, and a refresh that reset it would undo hand work every time it ran.
    /// </remarks>
    private static ImmutableArray<string> Differences(DbmlColumn current, DbmlColumnDraft draft)
    {
        var changes = ImmutableArray.CreateBuilder<string>();

        if (!string.Equals(current.ClrType, draft.ClrType, StringComparison.Ordinal))
            changes.Add($"Type {current.ClrType ?? "(none)"} → {draft.ClrType}");

        if (!string.Equals(current.DbType, draft.DbType, StringComparison.OrdinalIgnoreCase))
            changes.Add($"DbType {current.DbType ?? "(none)"} → {draft.DbType}");

        if (current.IsPrimaryKey != draft.IsPrimaryKey)
            changes.Add($"IsPrimaryKey {Word(current.IsPrimaryKey)} → {Word(draft.IsPrimaryKey)}");

        if (current.IsDbGenerated != draft.IsDbGenerated)
            changes.Add($"IsDbGenerated {Word(current.IsDbGenerated)} → {Word(draft.IsDbGenerated)}");

        if (current.IsVersion != draft.IsVersion)
            changes.Add($"IsVersion {Word(current.IsVersion)} → {Word(draft.IsVersion)}");

        if (current.CanBeNull != draft.CanBeNull)
            changes.Add($"CanBeNull {Word(current.CanBeNull)} → {Word(draft.CanBeNull)}");

        return changes.ToImmutable();
    }

    private static string Word(bool value) => value ? "true" : "false";

    /// <summary>
    /// The association pairs the database has and the model does not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A pair or nothing. An <c>&lt;Association&gt;</c> names a type in its <c>Type</c> attribute, so
    /// generating the child end of a key into a model that has no <c>&lt;Type&gt;</c> for the parent
    /// would write an element naming a class SqlMetal will not generate — a designer that does not
    /// compile, from a refresh that reported success. The pair is skipped and the reason goes into the
    /// plan's notes, because a silently missing relationship is the failure this is avoiding.
    /// </para>
    /// <para>
    /// Composite keys are written the way LINQ to SQL spells them, comma-separated in <c>ThisKey</c>
    /// and <c>OtherKey</c>, and the two lists are in the constraint's own column order — which is what
    /// pairs them up.
    /// </para>
    /// </remarks>
    private static ImmutableArray<DbmlAssociationDraft> PlanAssociations(
        DbTableSchema schema,
        IReadOnlyList<DbForeignKey> foreignKeys,
        DbmlDatabase database,
        ImmutableArray<string>.Builder notes)
    {
        var drafts = ImmutableArray.CreateBuilder<DbmlAssociationDraft>();

        // Names claimed so far, per type. Seeded from the file and then added to as the plan grows,
        // because two keys planned in one pass would otherwise both take the name the file left free.
        var claimed = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        string Take(string preferred, DbmlType type)
        {
            if (!claimed.TryGetValue(type.Name, out var names))
            {
                names = new HashSet<string>(
                    type.Columns.Select(c => c.Member).Concat(type.Associations.Select(a => a.Member)),
                    StringComparer.Ordinal);
                claimed[type.Name] = names;
            }

            string name = preferred;

            for (int suffix = 1; !names.Add(name) && suffix < 100; suffix++)
                name = $"{preferred}{suffix}";

            return name;
        }

        foreach (var key in foreignKeys)
        {
            var childTable = database.TableNamed(key.ParentTable);
            var parentTable = database.TableNamed(key.ReferencedTable);

            if (childTable?.RowType is not { } childType || parentTable?.RowType is not { } parentType)
            {
                string missing = childTable?.RowType is null ? key.ParentTable : key.ReferencedTable;
                notes.Add($"Skipped {key.Name}: the model has no <Table> for {missing}.");
                continue;
            }

            string name = AssociationName(key.Name);
            string thisKey = string.Join(", ", key.ParentColumns);
            string otherKey = string.Join(", ", key.ReferencedColumns);

            // The child holds the key and gets one parent; the parent is pointed at and gets many
            // children, named for the table's own member so the collection reads the way the
            // context's property does.
            if (!Modelled(childType, parentType.Name, thisKey, otherKey))
            {
                drafts.Add(new DbmlAssociationDraft(
                    OwnerTypeName: childType.Name,
                    Name: name,
                    Member: Take(parentType.Name, childType),
                    ThisKey: thisKey,
                    OtherKey: otherKey,
                    TargetTypeName: parentType.Name,
                    IsForeignKey: true,
                    IsCollection: false));
            }

            if (!Modelled(parentType, childType.Name, otherKey, thisKey))
            {
                drafts.Add(new DbmlAssociationDraft(
                    OwnerTypeName: parentType.Name,
                    Name: name,
                    Member: Take(childTable.Member, parentType),
                    ThisKey: otherKey,
                    OtherKey: thisKey,
                    TargetTypeName: childType.Name,
                    IsForeignKey: false,
                    IsCollection: true));
            }
        }

        return drafts.ToImmutable();
    }

    /// <summary>
    /// Whether the type already declares this end of this relationship.
    /// </summary>
    /// <remarks>
    /// <para>
    /// By shape — target type and the two key lists — and never by name. The name is the one part of
    /// an <c>&lt;Association&gt;</c> nothing depends on: it is free text a developer renames, and it
    /// is what a refresh itself rewrites when it trims a <c>FK_</c>. Matching on it means the second
    /// refresh of an unchanged table adds a duplicate of every relationship it added on the first,
    /// which is the bug this is written against. The keys and the target are what the mapping
    /// <em>is</em>, so two elements agreeing on all three are the same relationship whatever they are
    /// called.
    /// </para>
    /// <para>
    /// The keys are compared column by column rather than as strings, because
    /// <c>"A, B"</c> and <c>"A,B"</c> are the same key written by two different hands.
    /// </para>
    /// </remarks>
    private static bool Modelled(DbmlType type, string targetTypeName, string thisKey, string otherKey) =>
        type.Associations.Any(a =>
            string.Equals(a.TargetTypeName, targetTypeName, StringComparison.Ordinal)
            && SameKey(a.ThisKey, thisKey)
            && SameKey(a.OtherKey, otherKey));

    private static bool SameKey(string left, string right) =>
        Columns(left).SequenceEqual(Columns(right), StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> Columns(string key) =>
        key.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// The constraint's name without the <c>FK_</c> every SQL Server naming convention puts on the
    /// front of it.
    /// </summary>
    /// <remarks>
    /// The prefix says the constraint is a foreign key, which is the one thing an
    /// <c>&lt;Association&gt;</c> already says by being one. Trimmed only when something is left
    /// over, so a constraint actually called <c>FK_</c> keeps the name it has rather than losing it.
    /// </remarks>
    internal static string AssociationName(string constraintName) =>
        constraintName.Length > 3 && constraintName.StartsWith("FK_", StringComparison.OrdinalIgnoreCase)
            ? constraintName[3..]
            : constraintName;

}

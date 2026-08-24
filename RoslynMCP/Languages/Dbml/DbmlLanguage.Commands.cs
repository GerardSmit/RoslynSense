using System.Text.Json;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Language.Xml;
using RoslynMCP.Languages.Dbml.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Services.Database;

namespace RoslynMCP.Languages.Dbml;

internal sealed partial class DbmlLanguage : ILanguageCommandProvider
{
    /// <summary>The connections a refresh could run against.</summary>
    public const string ConnectionsCommand = "roslynSense.dbmlConnections";

    /// <summary><c>[uri, tableName, alias]</c> → what a refresh would do.</summary>
    public const string PlanRefreshCommand = "roslynSense.dbmlPlanRefresh";

    /// <summary><c>[uri, tableName, alias, includeRemovals]</c> → the write.</summary>
    public const string ApplyRefreshCommand = "roslynSense.dbmlApplyRefresh";

    /// <summary><c>[uri, alias]</c> → what the database has that the model does not.</summary>
    public const string AddableCommand = "roslynSense.dbmlAddable";

    /// <summary><c>[uri, alias, names]</c> → the write that adds them.</summary>
    public const string ApplyAddCommand = "roslynSense.dbmlApplyAdd";

    public bool CanExecute(string command) =>
        command is ConnectionsCommand or PlanRefreshCommand or ApplyRefreshCommand
            or AddableCommand or ApplyAddCommand;

    public async Task<object> ExecuteCommandAsync(ExecuteCommandParams p, CancellationToken ct) =>
        p.Command switch
        {
            ConnectionsCommand => Connections(),
            PlanRefreshCommand => await PlanAsync(p, ct),
            ApplyRefreshCommand => await ApplyAsync(p, ct),
            AddableCommand => await AddableAsync(p, ct),
            ApplyAddCommand => await ApplyAddAsync(p, ct),
            _ => new DbmlRefreshResult(false, $"Unknown command '{p.Command}'."),
        };

    /// <summary>
    /// The connections registered with the server, split by whether one can describe a schema.
    /// </summary>
    /// <remarks>
    /// The model's own <c>&lt;Connection&gt;</c> element is deliberately never read. It is a
    /// design-time artefact that commonly points at a machine that no longer exists, or carries a
    /// password, or names a production server — and a refresh silently connecting to whichever of
    /// those the file happens to name is the one way this feature could do real damage. RoslynSense's
    /// own registered connections are the ones the user configured on purpose.
    /// </remarks>
    private object Connections()
    {
        if (_connections is null)
            return new DbmlConnectionList([], []);

        var supported = new List<DbmlConnection>();
        var unsupported = new List<string>();

        foreach (var provider in _connections.All)
        {
            if (provider is IDbSchemaIntrospector)
                supported.Add(new DbmlConnection(provider.Alias, provider.ProviderName));
            else
                unsupported.Add(provider.Alias);
        }

        return new DbmlConnectionList([.. supported], [.. unsupported]);
    }

    private async Task<object> PlanAsync(ExecuteCommandParams p, CancellationToken ct)
    {
        if (await ResolveRefreshAsync(p, ct) is not { } request)
        {
            return new DbmlRefreshPlanResult(
                false, "The table, the model or the connection could not be resolved.");
        }

        var (view, table, introspector) = request;

        var schema = await introspector.DescribeTableSchemaAsync(table.Name, ct);

        if (schema is null)
        {
            return new DbmlRefreshPlanResult(
                false, $"The database has no table named {table.Name}.");
        }

        var keys = await introspector.ForeignKeysAsync(table.Name, ct);
        var plan = DbmlRefreshPlanner.Plan(table, schema, keys, view.Database);

        return new DbmlRefreshPlanResult(
            Ok: true,
            Message: plan.Summary,
            Table: plan.TableName,
            Added: [.. plan.Added.Select(c => new DbmlPlannedColumn(c.Name, c.DbType))],
            Updated: [.. plan.Updated.Select(u =>
                new DbmlPlannedColumn(u.Existing.Name, string.Join("; ", u.Changes)))],
            Removed: [.. plan.Removed.Select(c =>
                new DbmlPlannedColumn(c.Name, c.DbType ?? c.ClrType ?? string.Empty))],
            Associations: [.. plan.Associations.Select(a =>
                new DbmlPlannedColumn($"{a.OwnerTypeName}.{a.Member}", a.TargetTypeName))],
            Notes: [.. plan.Notes]);
    }

    /// <summary>
    /// Applies the plan: disk first, then the editor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order is the whole of the design. SqlMetal reads the file from disk, and
    /// <c>SolutionSessionService</c>'s watcher is what notices the write and regenerates the designer
    /// from it — so applying the change only to an unsaved buffer would regenerate the designer from
    /// the content that was there before, and the user would be looking at a model and a designer that
    /// disagree with no indication which is which.
    /// </para>
    /// <para>
    /// A dirty buffer that differs from disk is refused rather than resolved. Writing disk would leave
    /// the editor holding an older version it will happily save back over the refresh; writing the
    /// buffer would leave the regeneration reading stale text. Saying so and stopping is the only
    /// answer that does not lose an edit.
    /// </para>
    /// <para>
    /// Nothing is pushed into the editor's buffer, and that is the point of the check above rather
    /// than an omission. The buffer equals the disk by the time the write happens, so the editor
    /// reloads the file by itself — silently, and with no undo entry and no dirty marker. Sending a
    /// <c>workspace/applyEdit</c> as well would make the buffer dirty against a file that had already
    /// moved underneath it, which is exactly the "content of the file is newer" conflict the next
    /// Ctrl+S would report.
    /// </para>
    /// <para>
    /// No <c>SelfWriteTracker</c> note, unlike the other writers here: this write <em>should</em>
    /// retrigger the watcher, because regenerating the designer is the point.
    /// </para>
    /// </remarks>
    private async Task<object> ApplyAsync(ExecuteCommandParams p, CancellationToken ct)
    {
        bool includeRemovals = Argument(p, 3) is { ValueKind: JsonValueKind.True };

        if (await ResolveRefreshAsync(p, ct) is not { } request)
            return new DbmlRefreshResult(false, "The table, the model or the connection could not be resolved.");

        var (view, table, introspector) = request;

        var schema = await introspector.DescribeTableSchemaAsync(table.Name, ct);

        if (schema is null)
            return new DbmlRefreshResult(false, $"The database has no table named {table.Name}.");

        var keys = await introspector.ForeignKeysAsync(table.Name, ct);
        var plan = DbmlRefreshPlanner.Plan(table, schema, keys, view.Database);

        if (plan.IsEmpty)
            return new DbmlRefreshResult(true, plan.Summary);

        string path = view.FilePath;

        var (onDisk, problem) = await CleanTextAsync(path, ct);

        if (onDisk is null)
            return new DbmlRefreshResult(false, problem!);

        if (DbmlWriter.Apply(onDisk, plan, includeRemovals) is not { } refreshed)
            return new DbmlRefreshResult(false, $"{Path.GetFileName(path)} could not be rewritten.");

        if (await WriteModelAsync(path, refreshed, ct) is { } failure)
            return new DbmlRefreshResult(false, failure);

        string removals = includeRemovals || plan.Removed.IsEmpty
            ? string.Empty
            : $" {plan.Removed.Length} column(s) kept.";

        return new DbmlRefreshResult(true, plan.Summary + removals);
    }

    /// <summary>
    /// What the database has and the model does not — the picker's list.
    /// </summary>
    private async Task<object> AddableAsync(ExecuteCommandParams p, CancellationToken ct)
    {
        if (await ResolveModelAsync(p, ct) is not { } request)
            return new DbmlAddableList(false, "The model or the connection could not be resolved.");

        var (view, introspector) = request;

        var missing = DbmlAddPlanner.Missing(
            view.Database, await introspector.ListSchemaObjectsAsync(ct));

        // Grouped by kind for the picker, tables first — which is the enum's own order — and by
        // name within a kind, whatever schema the name is in.
        return new DbmlAddableList(
            true,
            missing.IsEmpty
                ? "Everything in the database is already in the model."
                : $"{missing.Length} object(s) can be added.",
            [.. missing
                .OrderBy(o => o.Kind)
                .ThenBy(o => o.QualifiedName, StringComparer.OrdinalIgnoreCase)
                .Select(o => new DbmlAddableObject(o.QualifiedName, KindWord(o.Kind)))]);
    }

    /// <summary>
    /// Adds the named objects: the elements first, then the associations they make possible.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The kinds are re-resolved from the catalogue rather than taken from the client, so the
    /// arguments stay names alone and a stale picker cannot write a view as a table.
    /// </para>
    /// <para>
    /// Associations are a second pass over the already-extended text on purpose: an added table's
    /// foreign key may point at another table added in the same batch, and planning against the text
    /// with every new <c>&lt;Type&gt;</c> in place is what lets that pair generate instead of being
    /// skipped as unmodelled. Disk-versus-buffer safety, the write and the cache invalidation are
    /// the refresh's own, for the reasons documented on <see cref="ApplyAsync"/>.
    /// </para>
    /// </remarks>
    private async Task<object> ApplyAddAsync(ExecuteCommandParams p, CancellationToken ct)
    {
        if (await ResolveModelAsync(p, ct) is not { } request)
            return new DbmlAddResult(false, "The model or the connection could not be resolved.");

        var (view, introspector) = request;

        if (Argument(p, 2) is not { ValueKind: JsonValueKind.Array } names)
            return new DbmlAddResult(false, "No objects were named.");

        var byName = new Dictionary<string, DbSchemaObject>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in await introspector.ListSchemaObjectsAsync(ct))
            byName[candidate.QualifiedName] = candidate;

        var notes = new List<string>();
        var tableSchemas = new List<(DbSchemaObject Object, DbTableSchema Schema)>();
        var functionSchemas = new List<DbFunctionSchema>();

        foreach (var element in names.EnumerateArray())
        {
            if (element.ValueKind is not JsonValueKind.String
                || element.GetString() is not { Length: > 0 } name)
            {
                continue;
            }

            if (!byName.TryGetValue(name, out var candidate))
                return new DbmlAddResult(false, $"The database has no object named {name}.");

            if (candidate.Kind is DbSchemaObjectKind.Table or DbSchemaObjectKind.View)
            {
                if (await introspector.DescribeTableSchemaAsync(
                        candidate.QualifiedName, ct) is not { } schema)
                {
                    return new DbmlAddResult(false, $"{name} could not be described.");
                }

                tableSchemas.Add((candidate, schema));
            }
            else
            {
                if (await introspector.DescribeFunctionAsync(
                        candidate.QualifiedName, ct) is not { } schema)
                {
                    return new DbmlAddResult(false, $"{name} could not be described.");
                }

                if (schema.Note is { Length: > 0 } note)
                    notes.Add(note);

                functionSchemas.Add(schema);
            }
        }

        var tables = DbmlAddPlanner.PlanTables(tableSchemas.Select(t => t.Schema), view.Database);
        var functions = DbmlAddPlanner.PlanFunctions(functionSchemas, view.Database);

        if (tables.IsEmpty && functions.IsEmpty)
            return new DbmlAddResult(true, "Nothing to add.");

        string path = view.FilePath;

        var (onDisk, problem) = await CleanTextAsync(path, ct);

        if (onDisk is null)
            return new DbmlAddResult(false, problem!);

        if (DbmlWriter.AddObjects(onDisk, tables, functions) is not { } extended)
            return new DbmlAddResult(false, $"{Path.GetFileName(path)} could not be rewritten.");

        int associations = 0;

        // Only real tables hold keys; a view's or a function's would be an empty answer paid for
        // with a round trip.
        foreach (var (candidate, schema) in tableSchemas)
        {
            if (candidate.Kind is not DbSchemaObjectKind.Table)
                continue;

            var keys = await introspector.ForeignKeysAsync(candidate.QualifiedName, ct);

            if (keys.Count == 0)
                continue;

            var database = DbmlReader.Read(Parser.ParseText(extended));

            if (database.TableNamed(schema.QualifiedName) is not { } table)
                continue;

            var plan = DbmlRefreshPlanner.Plan(table, schema, keys, database);

            notes.AddRange(plan.Notes);

            if (plan.IsEmpty)
                continue;

            if (DbmlWriter.Apply(extended, plan, includeRemovals: false) is { } withAssociations)
            {
                extended = withAssociations;
                associations += plan.Associations.Length;
            }
        }

        if (await WriteModelAsync(path, extended, ct) is { } failure)
            return new DbmlAddResult(false, failure);

        var parts = new List<string>();

        int views = tableSchemas.Count(t => t.Object.Kind is DbSchemaObjectKind.View);
        int added = tables.Length - views;

        if (added > 0) parts.Add($"{added} table(s)");
        if (views > 0) parts.Add($"{views} view(s)");
        if (!functions.IsEmpty) parts.Add($"{functions.Length} function(s)");
        if (associations > 0) parts.Add($"{associations} association(s)");

        return new DbmlAddResult(
            true, $"Added {string.Join(", ", parts)}.", notes.Count == 0 ? null : [.. notes]);
    }

    private static string KindWord(DbSchemaObjectKind kind) => kind switch
    {
        DbSchemaObjectKind.Table => "table",
        DbSchemaObjectKind.View => "view",
        DbSchemaObjectKind.ScalarFunction => "function",
        DbSchemaObjectKind.TableFunction => "table function",
        _ => "stored procedure",
    };

    /// <summary>
    /// The file's text when the editor and the disk agree, or the reason not to write.
    /// </summary>
    /// <remarks>
    /// A dirty buffer that differs from disk is refused rather than resolved, for the reason
    /// <see cref="ApplyAsync"/> gives: either write loses somebody's version of the file.
    /// </remarks>
    private static async Task<(string? Text, string? Problem)> CleanTextAsync(
        string path, CancellationToken ct)
    {
        string onDisk;

        try
        {
            onDisk = await File.ReadAllTextAsync(path, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"Could not read {Path.GetFileName(path)}: {ex.Message}");
        }

        if (OpenDocumentStore.TryGet(path, out var buffer)
            && !string.Equals(buffer.ToString(), onDisk, StringComparison.Ordinal))
        {
            return (null,
                $"{Path.GetFileName(path)} has unsaved changes. Save it and try again — "
                + "the designer is regenerated from the file on disk.");
        }

        return (onDisk, null);
    }

    /// <summary>The write and the cache invalidation, or what went wrong with the write.</summary>
    private static async Task<string?> WriteModelAsync(string path, string text, CancellationToken ct)
    {
        try
        {
            await File.WriteAllTextAsync(path, text, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Could not write {Path.GetFileName(path)}: {ex.Message}";
        }

        // The parse cache reads the buffer where there is one, and the buffer is about to be
        // replaced by the editor's own reload.
        DbmlDocumentCache.Invalidate(path);

        return null;
    }

    /// <summary>
    /// The model and the connection, for the commands that take the whole file rather than one
    /// table.
    /// </summary>
    private async Task<(DbmlView View, IDbSchemaIntrospector Introspector)?> ResolveModelAsync(
        ExecuteCommandParams p, CancellationToken ct)
    {
        if (Text(p, 0) is not { Length: > 0 } uri || Text(p, 1) is not { Length: > 0 } alias)
            return null;

        if (_connections?.Get(alias) is not IDbSchemaIntrospector introspector)
            return null;

        return await DbmlWorkspace.GetAsync(LspConverters.UriToPath(uri), ct) is { } view
            ? (view, introspector)
            : null;
    }

    /// <summary>
    /// The three things a refresh needs, or nothing when any of them is missing.
    /// </summary>
    private async Task<(DbmlView View, DbmlTable Table, IDbSchemaIntrospector Introspector)?>
        ResolveRefreshAsync(ExecuteCommandParams p, CancellationToken ct)
    {
        if (Text(p, 0) is not { Length: > 0 } uri
            || Text(p, 1) is not { Length: > 0 } tableName
            || Text(p, 2) is not { Length: > 0 } alias)
        {
            return null;
        }

        if (_connections?.Get(alias) is not IDbSchemaIntrospector introspector)
            return null;

        if (await DbmlWorkspace.GetAsync(LspConverters.UriToPath(uri), ct) is not { } view)
            return null;

        // By the model's own name for the table, so a caret in the file and the command agree even
        // when the database qualifies the name differently.
        return view.Database.TableNamed(tableName) is { } table ? (view, table, introspector) : null;
    }

    private static JsonElement? Argument(ExecuteCommandParams p, int index) =>
        p.Arguments is { } args && args.Length > index ? args[index] : null;

    private static string? Text(ExecuteCommandParams p, int index) =>
        Argument(p, index) is { ValueKind: JsonValueKind.String } element ? element.GetString() : null;
}

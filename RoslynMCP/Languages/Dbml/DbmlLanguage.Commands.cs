using System.Text.Json;
using Microsoft.CodeAnalysis.Text;
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

    public bool CanExecute(string command) =>
        command is ConnectionsCommand or PlanRefreshCommand or ApplyRefreshCommand;

    public async Task<object> ExecuteCommandAsync(ExecuteCommandParams p, CancellationToken ct) =>
        p.Command switch
        {
            ConnectionsCommand => Connections(),
            PlanRefreshCommand => await PlanAsync(p, ct),
            ApplyRefreshCommand => await ApplyAsync(p, ct),
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
        string onDisk;

        try
        {
            onDisk = await File.ReadAllTextAsync(path, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DbmlRefreshResult(false, $"Could not read {Path.GetFileName(path)}: {ex.Message}");
        }

        if (OpenDocumentStore.TryGet(path, out var buffer)
            && !string.Equals(buffer.ToString(), onDisk, StringComparison.Ordinal))
        {
            return new DbmlRefreshResult(
                false,
                $"{Path.GetFileName(path)} has unsaved changes. Save it and refresh again — "
                + "the designer is regenerated from the file on disk.");
        }

        if (DbmlWriter.Apply(onDisk, plan, includeRemovals) is not { } refreshed)
            return new DbmlRefreshResult(false, $"{Path.GetFileName(path)} could not be rewritten.");

        try
        {
            await File.WriteAllTextAsync(path, refreshed, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DbmlRefreshResult(false, $"Could not write {Path.GetFileName(path)}: {ex.Message}");
        }

        // The parse cache reads the buffer where there is one, and the buffer is about to be
        // replaced by the editor's own reload.
        DbmlDocumentCache.Invalidate(path);

        string removals = includeRemovals || plan.Removed.IsEmpty
            ? string.Empty
            : $" {plan.Removed.Length} column(s) kept.";

        return new DbmlRefreshResult(true, plan.Summary + removals);
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

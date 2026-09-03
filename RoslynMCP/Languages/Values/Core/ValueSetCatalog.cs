using System.Collections.Concurrent;
using System.Collections.Immutable;
using RoslynMCP.Services.Database;

namespace RoslynMCP.Languages.Values.Core;

/// <summary>
/// The values behind every set, loaded once and kept.
/// </summary>
/// <remarks>
/// <para>
/// A set is loaded the first time something asks about it and then never again until
/// <see cref="Refresh"/>. Not a time-based cache: a lookup table changes when someone deploys a
/// migration, which is neither often nor on a schedule, and a background poll would put a query on
/// a database behind every open editor for the rest of the day. A stale set is visibly stale — the
/// values it offers are the ones it shows on hover — and refreshing is one command.
/// </para>
/// <para>
/// Failures are cached too, and for the same reason. An unreachable server that is retried per
/// keystroke is an unreachable server that costs a connection timeout per keystroke; caching the
/// failure makes it one timeout, once, and then silence until asked again.
/// </para>
/// </remarks>
internal sealed class ValueSetCatalog
{
    /// <summary>
    /// Long enough for a cold connection pool, short enough that a wrong host does not hold a
    /// diagnostics pass open. The query itself is a <c>SELECT</c> over a lookup table.
    /// </summary>
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// More values than any completion list is worth, and far more than any set of status codes.
    /// A query that hits this is not a value set, and the cap is what stops it from being treated
    /// as one: past it the values are still offered but nothing is reported against them.
    /// </summary>
    private const int MaxValues = 2000;

    /// <summary>What a NULL first column renders as. See <c>DbProviderBase.FormatValue</c>.</summary>
    private const string NullText = "(null)";

    private readonly DbConnectionRegistry? _connections;

    private readonly ConcurrentDictionary<string, Lazy<Task<ValueSetContents>>> _loaded =
        new(StringComparer.OrdinalIgnoreCase);

    public ValueSetCatalog(DbConnectionRegistry? connections) => _connections = connections;

    /// <summary>
    /// The set's values, loading them if this is the first ask.
    /// </summary>
    /// <remarks>
    /// The caller's cancellation is applied to the <i>wait</i> and not to the load. Two editors
    /// asking at once share one query, and a request that is cancelled a keystroke later must not
    /// take the other one's answer down with it — which is exactly what passing the token through
    /// would do.
    /// </remarks>
    public Task<ValueSetContents> ContentsAsync(ValueSetDefinition set, CancellationToken ct)
    {
        var pending = _loaded.GetOrAdd(
            set.Id, _ => new Lazy<Task<ValueSetContents>>(() => LoadAsync(set)));

        return pending.Value.WaitAsync(ct);
    }

    /// <summary>
    /// What is already known about the set, without starting a load.
    /// </summary>
    /// <remarks>
    /// For the paths that must not block on a database: colouring, and any pass that runs per
    /// keystroke. A set nothing has loaded yet answers null, and the caller does nothing rather
    /// than something slow.
    /// </remarks>
    public ValueSetContents? Known(ValueSetDefinition set) =>
        _loaded.TryGetValue(set.Id, out var pending)
        && pending.IsValueCreated
        && pending.Value is { IsCompletedSuccessfully: true } task
            ? task.Result
            : null;

    /// <summary>Drops what is cached, so the next ask goes back to the database.</summary>
    /// <param name="id">One set, or null for all of them.</param>
    public void Refresh(string? id = null)
    {
        if (id is { Length: > 0 })
            _loaded.TryRemove(id, out _);
        else
            _loaded.Clear();
    }

    private async Task<ValueSetContents> LoadAsync(ValueSetDefinition set)
    {
        if (!set.FromDatabase)
            return ValueSetContents.Loaded(set, set.Inline, complete: true);

        if (Provider(set) is not { } resolved)
        {
            return ValueSetContents.Unavailable(
                set,
                set.Connection is { Length: > 0 } alias
                    ? $"No connection named '{alias}' is registered."
                    : _connections is null || _connections.All.Count == 0
                        ? "No database connections are registered."
                        : "Several connections are registered, so the set has to name one.");
        }

        try
        {
            using var timeout = new CancellationTokenSource(LoadTimeout);

            var result = await resolved.ExecuteQueryAsync(
                set.Query!, parameters: null, MaxValues, capturePlan: false, timeout.Token);

            return Read(set, result);
        }
        catch (OperationCanceledException)
        {
            return ValueSetContents.Unavailable(
                set, $"The query did not finish within {LoadTimeout.TotalSeconds:0} seconds.");
        }
        catch (Exception ex)
        {
            // Deliberately broad. Every provider throws its own exception type, and the only thing
            // any caller does with this is print it — a set that fails to load must never take the
            // request that asked for it down with it.
            return ValueSetContents.Unavailable(set, ex.Message);
        }
    }

    private IDbProvider? Provider(ValueSetDefinition set)
    {
        if (_connections is null)
            return null;

        if (set.Connection is { Length: > 0 } alias)
            return _connections.Get(alias);

        // One registered connection needs no naming; more than one does, because guessing which
        // database a set of codes lives in is not a guess worth making silently.
        var all = _connections.All;
        return all.Count == 1 ? all[0] : null;
    }

    /// <summary>
    /// The first column as the value, the second — if there is one — as its label.
    /// </summary>
    /// <remarks>
    /// Positional rather than by column name so a query needs no particular aliasing:
    /// <c>SELECT Code FROM …</c> and <c>SELECT Code, Description FROM …</c> both work as written.
    /// </remarks>
    private static ValueSetContents Read(ValueSetDefinition set, DbQueryResult result)
    {
        if (result.Columns.Length == 0)
            return ValueSetContents.Unavailable(set, "The query returned no columns.");

        var values = ImmutableArray.CreateBuilder<ValueEntry>(result.Rows.Count);
        var seen = new HashSet<string>(set.Comparer);
        bool labels = result.Columns.Length > 1;

        foreach (string[] row in result.Rows)
        {
            if (row.Length == 0 || row[0].Length == 0 || row[0] == NullText)
                continue;

            if (!seen.Add(row[0]))
                continue;

            string? label = labels && row.Length > 1 && row[1].Length > 0 && row[1] != NullText
                ? row[1]
                : null;

            values.Add(new ValueEntry(row[0], label));
        }

        if (values.Count == 0 && !result.Truncated)
        {
            return ValueSetContents.Unavailable(
                set, "The query returned no rows, so there is nothing to check against.");
        }

        return ValueSetContents.Loaded(
            set, values.ToImmutable(), complete: !result.Truncated,
            problem: result.Truncated
                ? $"More than {MaxValues} rows; the values are offered but nothing is reported "
                  + "against them."
                : null);
    }
}

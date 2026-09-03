namespace RoslynMCP.Services.Database;

/// <summary>
/// The database connections the db_* tools can reach, refreshable in place.
/// </summary>
/// <remarks>
/// Every connection is tracked with where it came from: registered at runtime
/// (<c>db_add_connection</c> or a seeding constructor), declared explicitly (roslynsense.json /
/// <c>--db</c>), or auto-discovered from config files. <see cref="ApplyResolved"/> replaces the
/// explicit and auto sets wholesale — which is what makes a config-file edit take effect on a
/// live host — while runtime entries always survive, and an alias removed with
/// <see cref="Remove"/> stays removed instead of being resurrected by the next refresh.
/// Precedence on an alias collision: runtime, then explicit, then auto.
/// </remarks>
public sealed class DbConnectionRegistry
{
    private enum Source { Runtime, Explicit, Auto }

    private sealed record Entry(IDbProvider Provider, Source Origin);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);

    public DbConnectionRegistry(IEnumerable<IDbProvider> providers)
    {
        foreach (var p in providers)
            _entries[p.Alias] = new Entry(p, Source.Runtime);
    }

    public IReadOnlyList<IDbProvider> All
    {
        get
        {
            lock (_gate)
                return _entries.Values
                    .Select(e => e.Provider)
                    .OrderBy(p => p.Alias, StringComparer.OrdinalIgnoreCase)
                    .ToList();
        }
    }

    public IDbProvider? Get(string alias)
    {
        lock (_gate)
            return _entries.TryGetValue(alias, out var e) ? e.Provider : null;
    }

    public bool TryAdd(IDbProvider provider)
    {
        lock (_gate)
        {
            if (_entries.ContainsKey(provider.Alias)) return false;
            _entries[provider.Alias] = new Entry(provider, Source.Runtime);
            _removed.Remove(provider.Alias);
            return true;
        }
    }

    public void AddOrReplace(IDbProvider provider)
    {
        lock (_gate)
        {
            _entries[provider.Alias] = new Entry(provider, Source.Runtime);
            _removed.Remove(provider.Alias);
        }
    }

    public bool Remove(string alias)
    {
        lock (_gate)
        {
            if (!_entries.Remove(alias)) return false;
            _removed.Add(alias);
            return true;
        }
    }

    /// <summary>
    /// Replaces the explicit and auto-discovered connections with a freshly resolved set,
    /// keeping runtime entries and honouring earlier removals.
    /// </summary>
    /// <returns>Human-readable descriptions of what changed; empty when nothing did.</returns>
    public IReadOnlyList<string> ApplyResolved(
        IReadOnlyList<IDbProvider> explicitProviders, IReadOnlyList<IDbProvider> autoProviders)
    {
        lock (_gate)
        {
            var fresh = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (var (alias, entry) in _entries)
                if (entry.Origin == Source.Runtime)
                    fresh[alias] = entry;

            foreach (var p in explicitProviders)
                AddResolved(fresh, p, Source.Explicit);
            foreach (var p in autoProviders)
                AddResolved(fresh, p, Source.Auto);

            var changes = Describe(_entries, fresh);
            _entries.Clear();
            foreach (var (alias, entry) in fresh)
                _entries[alias] = entry;
            return changes;
        }
    }

    private void AddResolved(Dictionary<string, Entry> fresh, IDbProvider provider, Source origin)
    {
        if (_removed.Contains(provider.Alias)) return;
        if (fresh.ContainsKey(provider.Alias)) return;

        // Keep the existing instance when nothing about it changed, so an unrelated file event
        // does not silently swap providers under a caller.
        if (_entries.TryGetValue(provider.Alias, out var old) &&
            old.Origin == origin &&
            string.Equals(Fingerprint(old.Provider), Fingerprint(provider), StringComparison.Ordinal))
        {
            fresh[provider.Alias] = old;
            return;
        }

        fresh[provider.Alias] = new Entry(provider, origin);
    }

    private static IReadOnlyList<string> Describe(
        Dictionary<string, Entry> before, Dictionary<string, Entry> after)
    {
        var changes = new List<string>();
        foreach (var (alias, entry) in after.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!before.TryGetValue(alias, out var old))
                changes.Add($"added '{alias}' ({entry.Provider.ProviderName})");
            else if (!string.Equals(Fingerprint(old.Provider), Fingerprint(entry.Provider), StringComparison.Ordinal))
                changes.Add($"updated '{alias}'");
        }
        foreach (var alias in before.Keys.OrderBy(a => a, StringComparer.OrdinalIgnoreCase))
        {
            if (!after.ContainsKey(alias))
                changes.Add($"removed '{alias}'");
        }
        return changes;
    }

    /// <summary>What makes two providers "the same connection": provider kind plus the raw
    /// connection string when the implementation exposes one.</summary>
    private static string Fingerprint(IDbProvider provider) =>
        provider.ProviderName + "\n" +
        (provider is DbProviderBase b ? b.ConnectionString : provider.GetType().FullName);
}

namespace RoslynMCP.Services.Memory;

/// <summary>Collectible semantic payloads with bounded metadata and a small idle-expiring working set.</summary>
internal sealed class WeakSnapshotCache<TKey, TValue> : IMemoryCache where TKey : notnull where TValue : class
{
    private sealed class Entry(TValue value, long used)
    {
        public readonly WeakReference<TValue> Weak = new(value);
        public TValue? Strong;
        public long Used = used;
    }

    private readonly object _gate = new();
    private readonly Dictionary<TKey, Entry> _entries = new();
    // Stripes serialize builds without a permanent table of tasks/closures or keys.
    private readonly SemaphoreSlim[] _buildGates = Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1)).ToArray();
    private readonly string _name;
    private readonly int _strongLimit;
    private readonly int _entryLimit;
    private readonly TimeSpan _idle;
    private readonly TimeProvider _time;
    private long _generation, _hits, _misses;

    public WeakSnapshotCache(string name, int strongLimit, int entryLimit = 4096,
        TimeSpan? idle = null, TimeProvider? time = null)
    {
        _name = name;
        _strongLimit = strongLimit;
        _entryLimit = entryLimit;
        _idle = idle ?? TimeSpan.FromSeconds(60);
        _time = time ?? TimeProvider.System;
        MemoryCacheRegistry.Register(this);
    }

    public SemaphoreSlim BuildGate(TKey key) => _buildGates[(uint)key.GetHashCode() % (uint)_buildGates.Length];
    public long Generation { get { lock (_gate) return _generation; } }

    public bool TryGet(TKey key, out TValue value, bool retain = false)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry) && entry.Weak.TryGetTarget(out value!))
            {
                _hits++;
                if (retain)
                {
                    entry.Strong = value;
                    entry.Used = _time.GetTimestamp();
                    TrimLocked();
                }
                return true;
            }
            _misses++;
            value = null!;
            return false;
        }
    }

    public void Set(TKey key, TValue value, long generation, bool retain = false)
    {
        lock (_gate)
        {
            if (generation != _generation) return;
            _entries[key] = new(value, _time.GetTimestamp()) { Strong = retain ? value : null };
            TrimLocked();
        }
    }

    public void RemoveWhere(Func<TKey, bool> predicate)
    {
        lock (_gate)
        {
            _generation++;
            foreach (var key in _entries.Keys.Where(predicate).ToArray()) _entries.Remove(key);
        }
    }
    public void Clear() { lock (_gate) { _generation++; _entries.Clear(); } }
    public void ReleaseStrong() { lock (_gate) foreach (var entry in _entries.Values) entry.Strong = null; }
    public void WorkspaceChanged() => Clear();
    public void Trim() { lock (_gate) TrimLocked(); }

    private void TrimLocked()
    {
        var now = _time.GetTimestamp();
        foreach (var (key, entry) in _entries.ToArray())
        {
            if (_time.GetElapsedTime(entry.Used, now) >= _idle) entry.Strong = null;
            if (!entry.Weak.TryGetTarget(out _)) _entries.Remove(key);
        }
        foreach (var entry in _entries.Values.Where(e => e.Strong is not null)
                     .OrderByDescending(e => e.Used).Skip(_strongLimit)) entry.Strong = null;
        foreach (var key in _entries.OrderByDescending(e => e.Value.Used).Skip(_entryLimit).Select(e => e.Key).ToArray())
            _entries.Remove(key);
    }

    public CacheMemoryInfo Inspect()
    {
        lock (_gate) return new(_name, _entries.Count, _entries.Values.Count(e => e.Weak.TryGetTarget(out _)),
            _entries.Values.Count(e => e.Strong is not null), _hits, _misses);
    }
}

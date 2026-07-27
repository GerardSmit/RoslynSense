using System.Collections.Concurrent;

namespace RoslynMCP.Services.Memory;

/// <summary>
/// In-memory store for managed heap snapshots taken with <see cref="MemorySnapshotService"/>.
/// Retains per-type aggregates so snapshots can be compared to find what grew.
/// Snapshots auto-expire after 30 minutes of inactivity, like profiling sessions.
/// </summary>
public sealed class MemorySnapshotStore
{
    public record TypeStat(string TypeName, long Count, long TotalBytes);

    public record HeapSnapshot(
        string Id,
        string Description,
        DateTime CapturedAt,
        int Pid,
        long TotalHeapBytes,
        long ObjectCount,
        long Gen0Bytes,
        long Gen1Bytes,
        long Gen2Bytes,
        long LargeObjectBytes,
        long PinnedObjectBytes,
        Dictionary<string, TypeStat> ByType);

    /// <summary>The per-type difference between two snapshots, ordered by bytes grown.</summary>
    public record TypeDelta(
        string TypeName,
        long CountDelta,
        long BytesDelta,
        long BaseCount,
        long BaseBytes,
        long TargetCount,
        long TargetBytes);

    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, (HeapSnapshot Snapshot, DateTime LastAccessed)> _snapshots = new();

    /// <summary>Stores a snapshot and returns its ID.</summary>
    public string Store(HeapSnapshot snapshot)
    {
        EvictExpired();
        _snapshots[snapshot.Id] = (snapshot, DateTime.UtcNow);
        return snapshot.Id;
    }

    public static string NewId() => $"heap-{DateTime.UtcNow:HHmmss}-{Guid.NewGuid().ToString()[..4]}";

    /// <summary>Gets a snapshot by ID, refreshing its expiry.</summary>
    public HeapSnapshot? Get(string snapshotId)
    {
        if (_snapshots.TryGetValue(snapshotId, out var entry))
        {
            _snapshots[snapshotId] = (entry.Snapshot, DateTime.UtcNow);
            return entry.Snapshot;
        }
        return null;
    }

    /// <summary>Lists all active snapshots, newest first.</summary>
    public IReadOnlyList<HeapSnapshot> List()
    {
        EvictExpired();
        return _snapshots.Values
            .OrderByDescending(e => e.Snapshot.CapturedAt)
            .Select(e => e.Snapshot)
            .ToList();
    }

    /// <summary>
    /// Computes per-type growth between two snapshots. Types present in only one snapshot are
    /// treated as zero in the other, so new and collected types both surface.
    /// </summary>
    public static List<TypeDelta> Diff(HeapSnapshot baseline, HeapSnapshot target, int maxResults)
    {
        var deltas = new List<TypeDelta>();

        foreach (var typeName in baseline.ByType.Keys.Union(target.ByType.Keys))
        {
            var before = baseline.ByType.GetValueOrDefault(typeName);
            var after = target.ByType.GetValueOrDefault(typeName);

            long countDelta = (after?.Count ?? 0) - (before?.Count ?? 0);
            long bytesDelta = (after?.TotalBytes ?? 0) - (before?.TotalBytes ?? 0);

            if (countDelta == 0 && bytesDelta == 0)
                continue;

            deltas.Add(new TypeDelta(
                typeName, countDelta, bytesDelta,
                before?.Count ?? 0, before?.TotalBytes ?? 0,
                after?.Count ?? 0, after?.TotalBytes ?? 0));
        }

        // Growth first — that is what a leak investigation is after — then the biggest shrinkers.
        deltas.Sort((a, b) => b.BytesDelta.CompareTo(a.BytesDelta));

        return deltas.Count > maxResults ? deltas.GetRange(0, maxResults) : deltas;
    }

    private void EvictExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var key in _snapshots.Keys)
        {
            if (_snapshots.TryGetValue(key, out var entry) && now - entry.LastAccessed > SnapshotTtl)
                _snapshots.TryRemove(key, out _);
        }
    }
}

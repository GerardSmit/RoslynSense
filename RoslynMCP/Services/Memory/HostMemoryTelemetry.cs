using System.Collections.Concurrent;
using System.Diagnostics;

namespace RoslynMCP.Services.Memory;

/// <summary>Scalar observations only: observing a snapshot must not extend its lifetime.</summary>
internal static class HostMemoryTelemetry
{
    private static long _active;
    private static readonly object ActivityGate = new();
    private static readonly ConcurrentDictionary<string, Samples> Timings = new();
    private sealed class Samples
    {
        public readonly Queue<double> Milliseconds = new();
        public long Count;
    }
    public static long ActiveOperations => Interlocked.Read(ref _active);
    public static IDisposable Operation(string name) => new Measurement(name);
    private sealed class Measurement(string name) : IDisposable
    {
        private readonly long _started = Begin();
        private int _disposed;
        private static long Begin() { lock (ActivityGate) Interlocked.Increment(ref _active); return Stopwatch.GetTimestamp(); }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Interlocked.Decrement(ref _active);
            var samples = Timings.GetOrAdd(name, _ => new());
            lock (samples)
            {
                samples.Count++;
                samples.Milliseconds.Enqueue(Stopwatch.GetElapsedTime(_started).TotalMilliseconds);
                while (samples.Milliseconds.Count > 256) samples.Milliseconds.Dequeue();
            }
        }
    }
    public static async Task<T> TrackAsync<T>(string name, Func<Task<T>> action)
    {
        using var operation = Operation(name);
        return await action().ConfigureAwait(false);
    }
    public static async Task TrackAsync(string name, Func<Task> action)
    {
        using var operation = Operation(name);
        await action().ConfigureAwait(false);
    }

    // Synchronous timer maintenance holds this only while retiring idle entries. Incoming
    // requests enter after retirement, rather than racing the zero-active-count observation.
    public static IDisposable? TryBeginIdleMaintenance()
    {
        if (!Monitor.TryEnter(ActivityGate)) return null;
        if (ActiveOperations != 0) { Monitor.Exit(ActivityGate); return null; }
        return new MaintenanceLease();
    }
    private sealed class MaintenanceLease : IDisposable
    {
        public void Dispose() => Monitor.Exit(ActivityGate);
    }

    public static object Capture()
    {
        using var process = Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        return new
        {
            TimestampUtc = DateTime.UtcNow, Pid = process.Id,
            Runtime = Environment.Version.ToString(), BuildMvid = typeof(HostMemoryTelemetry).Module.ModuleVersionId,
            process.PrivateMemorySize64, process.WorkingSet64,
            Children = ChildProcessMemory.Capture(process.Id),
            ActiveOperations, AllocatedBytes = GC.GetTotalAllocatedBytes(),
            CollectionCounts = Enumerable.Range(0, GC.MaxGeneration + 1).Select(GC.CollectionCount).ToArray(),
            Gc = new
            {
                gc.Index, gc.Generation, gc.Concurrent, gc.Compacted,
                gc.HeapSizeBytes, gc.TotalCommittedBytes, gc.FragmentedBytes, gc.PauseTimePercentage,
                Server = System.Runtime.GCSettings.IsServerGC,
                Latency = System.Runtime.GCSettings.LatencyMode.ToString(),
                Generations = gc.GenerationInfo.ToArray().Select(g => new
                { g.SizeBeforeBytes, g.SizeAfterBytes, g.FragmentationBeforeBytes, g.FragmentationAfterBytes }).ToArray(),
                PausesMs = gc.PauseDurations.ToArray().Select(t => t.TotalMilliseconds).ToArray(),
            },
            Caches = MemoryCacheRegistry.Inspect(),
            Workspaces = WorkspaceService.MemoryInventory(),
            HotReload = HotReload.HotReloadService.MemoryInventory(),
            Timings = Timings.Select(pair =>
            {
                lock (pair.Value)
                {
                    var sorted = pair.Value.Milliseconds.Order().ToArray();
                    return new { Name = pair.Key, pair.Value.Count, Samples = sorted.Length,
                        P50Ms = sorted.Length == 0 ? 0 : sorted[(sorted.Length - 1) / 2],
                        P95Ms = sorted.Length == 0 ? 0 : sorted[(int)((sorted.Length - 1) * .95)] };
                }
            }).ToArray(),
        };
    }
}

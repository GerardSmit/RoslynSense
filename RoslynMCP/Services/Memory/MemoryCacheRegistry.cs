namespace RoslynMCP.Services.Memory;

internal sealed record CacheMemoryInfo(string Name, int Entries, int LiveTargets, int StrongEntries,
    long Hits = 0, long Misses = 0);

internal interface IMemoryCache
{
    CacheMemoryInfo Inspect();
    void Trim();
    void DocumentChanged(string path) { }
    void WorkspaceChanged() { }
}

/// <summary>Idle maintenance must not itself keep a disconnected editor or its caches alive.</summary>
internal static class MemoryCacheRegistry
{
    private static readonly object Gate = new();
    private static readonly List<WeakReference<IMemoryCache>> Caches = [];
    private static readonly Timer Timer = new(_ => Visit(c => c.Trim()), null,
        TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));

    public static void Register(IMemoryCache cache)
    {
        lock (Gate)
        {
            Caches.RemoveAll(w => !w.TryGetTarget(out _));
            Caches.Add(new(cache));
        }
    }

    private static void Visit(Action<IMemoryCache> action)
    {
        IMemoryCache[] live;
        lock (Gate)
        {
            Caches.RemoveAll(w => !w.TryGetTarget(out _));
            live = Caches.Select(w => w.TryGetTarget(out var c) ? c : null).OfType<IMemoryCache>().ToArray();
        }
        foreach (var cache in live)
        {
            try { action(cache); }
            catch (Exception ex) { Console.Error.WriteLine($"[MemoryCache] {ex.Message}"); }
        }
    }

    public static CacheMemoryInfo[] Inspect()
    {
        var result = new List<CacheMemoryInfo>();
        Visit(c => result.Add(c.Inspect()));
        return result.ToArray();
    }
    public static void Trim() => Visit(c => c.Trim());
    public static void DocumentChanged(string path) => Visit(c => c.DocumentChanged(path));
    public static void WorkspaceChanged() => Visit(c => c.WorkspaceChanged());
}

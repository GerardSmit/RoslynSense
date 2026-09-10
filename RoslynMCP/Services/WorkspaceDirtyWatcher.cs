using System.Collections.Concurrent;

namespace RoslynMCP.Services;

/// <summary>
/// Watches a workspace's directory tree and remembers which source files changed on disk, so a
/// request can refresh exactly those documents instead of statting every file it might care
/// about. This is what keeps the daemon honest for edits made outside any editor buffer — a
/// git checkout, a code generator, an MCP client writing files — where nothing sends a didChange
/// and the workspace would otherwise answer with the text it loaded, at the line numbers it
/// loaded it.
/// </summary>
/// <remarks>
/// Events are recorded, never acted on: the consumer decides what a path means (a document, an
/// open buffer someone else owns, noise under <c>obj\</c>) at the moment it asks, with the
/// workspace lock it already holds. Each recorded path carries a generation stamp so a consumer
/// can clear exactly the event it handled and never one that arrived while it was working.
/// A watcher that cannot start, or whose buffer overflows, degrades to the caller's stat sweep —
/// missing an event must never mean missing the edit forever.
/// </remarks>
internal sealed class WorkspaceDirtyWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly ConcurrentDictionary<string, long> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private long _generation;
    private int _overflowed;

    private WorkspaceDirtyWatcher(FileSystemWatcher watcher) => _watcher = watcher;

    /// <summary>A watcher over <paramref name="rootDirectory"/>, or <c>null</c> when one cannot
    /// be started there (network shares and deleted directories are the usual reasons).</summary>
    public static WorkspaceDirtyWatcher? TryCreate(string? rootDirectory)
    {
        if (rootDirectory is not { Length: > 0 } || !Directory.Exists(rootDirectory))
            return null;

        try
        {
            var fsw = new FileSystemWatcher(rootDirectory, "*.cs")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };

            var watcher = new WorkspaceDirtyWatcher(fsw);
            fsw.Changed += (_, e) => watcher.Mark(e.FullPath);
            fsw.Created += (_, e) => watcher.Mark(e.FullPath);
            fsw.Renamed += (_, e) =>
            {
                watcher.Mark(e.OldFullPath);
                watcher.Mark(e.FullPath);
            };
            // Deletions matter to project structure, not to document text; WatchedFilesHandler
            // owns that. Overflow means events were dropped, so only a full sweep is safe.
            fsw.Error += (_, _) => watcher.MarkOverflow();
            fsw.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Mark(string path) => _dirty[path] = Interlocked.Increment(ref _generation);

    /// <summary>True once per overflow: the caller must fall back to a full sweep, because an
    /// unknown number of events never made it into the dirty set.</summary>
    public bool TakeOverflow() => Interlocked.Exchange(ref _overflowed, 0) != 0;

    /// <summary>Requests a full sweep, also used when a sweep could not commit its changes.</summary>
    public void MarkOverflow() => Interlocked.Exchange(ref _overflowed, 1);

    /// <summary>The paths currently marked dirty, each with the stamp to hand back to
    /// <see cref="Clear"/> once handled.</summary>
    public IReadOnlyList<KeyValuePair<string, long>> Snapshot() => [.. _dirty];

    /// <summary>Forgets one handled event — only if no newer event landed on the same path
    /// while the caller was applying this one.</summary>
    public void Clear(KeyValuePair<string, long> handled) =>
        ((ICollection<KeyValuePair<string, long>>)_dirty).Remove(handled);

    public void Dispose()
    {
        try { _watcher.Dispose(); }
        catch (Exception)
        {
            // A watcher on a directory that vanished can throw on teardown; there is nothing
            // left to release when it does.
        }
    }
}

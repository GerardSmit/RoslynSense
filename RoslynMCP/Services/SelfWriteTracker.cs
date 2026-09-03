using System.Collections.Concurrent;

namespace RoslynMCP.Services;

/// <summary>
/// Remembers files this server just wrote, so the machinery that watches for outside changes does
/// not react to them.
/// </summary>
/// <remarks>
/// <para>
/// Every mutating operation — adding a package, editing the solution tree, scaffolding a file —
/// invalidates exactly what it knows it changed, and then writes a <c>.sln</c> or <c>.csproj</c> to
/// disk. The file watcher sees that write a moment later, cannot tell it apart from someone editing
/// the project file in another editor, and invalidates everything a second time. So each of those
/// operations cost two full reloads: the one it asked for, and one it caused.
/// </para>
/// <para>
/// Recognition is by the file's own stamp — size and modification time, recorded after the write —
/// so what is being asked is "is this still exactly what we put there", not "did we write
/// something recently". Anything that no longer matches is an outside change and is honoured,
/// which is the safe direction to be wrong in: a missed suppression costs a redundant reload,
/// while suppressing a real edit would leave the workspace stale. See
/// <see cref="WasWrittenByUs"/> for why a time window is the wrong question.
/// </para>
/// </remarks>
public static class SelfWriteTracker
{
    /// <summary>How many recent writes to remember. A write only has to be recognised for as long
    /// as it takes the watcher to report it back, so this is a ceiling rather than a working set —
    /// forgetting an old one costs at most one redundant reload.</summary>
    private const int MaxEntries = 512;

    private static readonly ConcurrentDictionary<string, (long Ticks, long Length, long Recorded)> s_written =
        new(StringComparer.OrdinalIgnoreCase);

    private static long s_recordClock;

    /// <summary>
    /// Records that this server wrote <paramref name="path"/>. Call it <em>after</em> the write, so
    /// the stamp describes what landed on disk.
    /// </summary>
    public static void Note(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        string key = Normalize(path);
        try
        {
            var info = new FileInfo(key);
            if (info.Exists)
                s_written[key] = (
                    info.LastWriteTimeUtc.Ticks, info.Length, Interlocked.Increment(ref s_recordClock));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        Prune();
    }

    /// <summary>
    /// Whether the file on disk is still exactly what this server last wrote there.
    /// </summary>
    /// <remarks>
    /// Identity, not recency. A time window has to guess how long the watcher will take to report
    /// the echo, and it is wrong in both directions: too short and the redundant reload it exists
    /// to prevent happens anyway; too long and a real edit arriving moments after our own write is
    /// discarded — with nothing to re-check it, leaving the workspace stale until something else
    /// evicts it. The file's own stamp answers the actual question, which is whether anything has
    /// happened to it since.
    /// </remarks>
    public static bool WasWrittenByUs(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        string key = Normalize(path);
        if (!s_written.TryGetValue(key, out var stamp))
            return false;

        try
        {
            var info = new FileInfo(key);
            return info.Exists
                && info.LastWriteTimeUtc.Ticks == stamp.Ticks
                && info.Length == stamp.Length;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Test hook: forgets everything, so one case cannot suppress another's events.</summary>
    internal static void ResetForTests() => s_written.Clear();

    private static void Prune()
    {
        if (s_written.Count < MaxEntries)
            return;

        // Oldest first, by when we wrote them. Pruning by "does the stamp still match" removed
        // almost nothing — a file we wrote and nobody touched matches forever — so the set only
        // grew, and past the threshold every Note() re-stat'd the whole thing. A refactoring that
        // renames eight hundred files did that eight hundred times.
        foreach (var (path, _) in s_written.OrderBy(e => e.Value.Recorded).Take(s_written.Count - MaxEntries / 2))
            s_written.TryRemove(path, out _);
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }
}

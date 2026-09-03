using System.Collections.Concurrent;
using RoslynItem = Microsoft.CodeAnalysis.Completion.CompletionItem;

namespace RoslynMCP.Lsp.Completion;

/// <summary>
/// What the user actually picked, per context. Deliberately weak: it only promotes one item
/// inside a relevance tier and never lifts an item across tiers, so a stray pick cannot bury the
/// obviously-right member — the property that makes ReSharper's list feel stable rather than
/// slot-machine-like.
/// </summary>
/// <remarks>
/// In memory for the life of the server process; a completion list is not worth a database. The
/// accept signal arrives as the <c>roslynSense.completionAccepted</c> command that rides along
/// with each item and which the client executes after inserting it.
/// </remarks>
public static class CompletionStatistics
{
    private const int MaxEntries = 20_000;

    private static readonly ConcurrentDictionary<(string Context, string Identity), Entry> s_entries = new();
    private static long s_clock;

    private sealed class Entry
    {
        public int Count;
        public long LastUsed;
    }

    public static void Record(string contextId, string identity)
    {
        if (string.IsNullOrEmpty(identity))
            return;

        if (s_entries.Count >= MaxEntries)
            Reset();

        var entry = s_entries.GetOrAdd((contextId, identity), static _ => new Entry());
        Interlocked.Increment(ref entry.Count);
        entry.LastUsed = Interlocked.Increment(ref s_clock);
    }

    /// <summary>
    /// Usage score, higher wins, -1 for never used. Uses beat recency, recency breaks ties.
    /// </summary>
    public static long Score(string contextId, string identity)
    {
        if (!s_entries.TryGetValue((contextId, identity), out var entry))
            return -1;
        return ((long)entry.Count << 32) | (uint)Math.Min(entry.LastUsed, uint.MaxValue);
    }

    public static void Reset()
    {
        s_entries.Clear();
        Interlocked.Exchange(ref s_clock, 0);
    }

    /// <summary>
    /// Identity of an item across completion sessions. Display text plus its first tag: stable
    /// across re-evaluations, and specific enough that a method and a type of the same name are
    /// tracked apart.
    /// </summary>
    public static string Identity(RoslynItem item) =>
        item.Tags.Length > 0 ? $"{item.Tags[0]}:{item.DisplayText}" : item.DisplayText;
}

using System.Collections.Concurrent;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Lsp;

/// <summary>
/// Keeps a file's inheritance gutter markers for as long as the state they were computed from is
/// unchanged.
/// </summary>
/// <remarks>
/// <para>
/// The markers cost what a code lens costs — <see cref="Handlers.InheritanceMarkersHandler"/> runs
/// up to fifty workspace-wide <c>SymbolFinder</c> queries for one file — and the client asks for
/// the whole array far more often than a lens is resolved. The extension refreshes them when the
/// active editor changes, 700 ms after the last keystroke, once when the client starts, and again
/// in full every time the user clicks one marker, because reading a single line's targets means
/// re-requesting the array that holds them.
/// </para>
/// <para>
/// So this is <see cref="CodeLensResolveMemo"/>'s shape applied to the one caller that reaches the
/// same computation by another route: the same <c>ComputeDownTargetsAsync</c>, keyed by the same
/// <see cref="DocumentSemanticGeneration"/>, bounded the same way. It makes the editor-switch,
/// client-start and click-a-marker paths free. It does <em>not</em> make the post-edit refresh
/// free — a keystroke moves the text version, which is exactly what the key is supposed to notice.
/// </para>
/// <para>
/// Bounded by file count rather than evicted precisely, because an entry pins a whole
/// <c>Solution</c> snapshot through its generation. Dropping one costs a recomputation, never a
/// wrong answer.
/// </para>
/// </remarks>
internal static class InheritanceMarkerMemo
{
    /// <summary>How many files to keep answers for. Matches
    /// <see cref="CodeLensResolveMemo"/>'s cap, and for the same reason.</summary>
    private const int MaxFiles = 8;

    private sealed record Entry(object Generation, Lazy<Task<InheritanceMarker[]>> Markers)
    {
        /// <summary>When this file was last asked about, for evicting the least recent first.</summary>
        public long Touched;
    }

    private static readonly ConcurrentDictionary<string, Entry> s_byUri =
        new(StringComparer.OrdinalIgnoreCase);

    private static long s_clock;

    /// <summary>
    /// The markers for <paramref name="uri"/>, computed by <paramref name="compute"/> when nothing
    /// is held for <paramref name="generation"/>.
    /// </summary>
    public static async Task<InheritanceMarker[]> GetAsync(
        string uri, object generation, Func<Task<InheritanceMarker[]>> compute, CancellationToken ct)
    {
        // Lazy rather than a bare task: ConcurrentDictionary does not hold its lock across the
        // factory, so two refreshes racing on one file would otherwise both start fifty workspace
        // searches and one full result would be computed only to be discarded.
        var entry = s_byUri.AddOrUpdate(
            uri,
            _ => new Entry(generation, Defer(compute)),
            (_, existing) => existing.Generation.Equals(generation)
                ? existing
                : new Entry(generation, Defer(compute)));

        Volatile.Write(ref entry.Touched, Interlocked.Increment(ref s_clock));

        if (s_byUri.Count > MaxFiles)
        {
            foreach (var stale in s_byUri.ToArray()
                         .OrderBy(pair => Volatile.Read(ref pair.Value.Touched))
                         .Take(s_byUri.Count - MaxFiles))
            {
                s_byUri.TryRemove(stale.Key, out _);
            }
        }

        // Not cancelled by this request: another refresh may be waiting on the same entry, and one
        // editor switch being abandoned must not make the others start over.
        return await entry.Markers.Value.WaitAsync(ct);
    }

    private static Lazy<Task<InheritanceMarker[]>> Defer(Func<Task<InheritanceMarker[]>> compute) =>
        new(compute, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Drops every kept answer. For tests that need a cold measurement.</summary>
    internal static void Clear() => s_byUri.Clear();
}

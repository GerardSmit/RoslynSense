namespace RoslynMCP.Lsp;

/// <summary>
/// Decides when a batch of background passes has earned a client refresh: at the first result
/// that changed something, then at most once per interval while the batch is still running, and
/// once more when it settles with an unannounced change.
/// </summary>
/// <remarks>
/// <para>
/// A refresh makes the editor re-pull every file's diagnostics, and that sweep is what queues the
/// next round of background passes. Refreshing per changed document therefore fed the sweep its
/// own output: a rebuild that moved the semantic version of a large project queued a pass for
/// every closed file, each completion asked for a refresh, and the refreshes ran at hundreds per
/// ten minutes for as long as the passes kept landing — a daemon at four gigabytes and an editor
/// whose result ids never stopped churning.
/// </para>
/// <para>
/// The counter is of passes in flight, entered when queued rather than when started, so a batch
/// waiting on the recompute slots counts as one batch and not as a series of settled ones.
/// </para>
/// </remarks>
internal sealed class RefreshBatch(TimeSpan interval, Func<long>? clock = null)
{
    private readonly Func<long> _clock = clock ?? (static () => Environment.TickCount64);
    private readonly long _intervalMs = (long)interval.TotalMilliseconds;

    private int _pending;
    private int _changed;

    // Far enough in the past that the first change in a session refreshes at once: a result
    // landing quickly is the difference between squiggles appearing and a "Loading..." hover.
    private long _lastRefreshAt = long.MinValue / 2;

    /// <summary>A pass was queued.</summary>
    public void Enter() => Interlocked.Increment(ref _pending);

    /// <summary>
    /// A pass finished. Returns whether the caller should ask the client to refresh now.
    /// </summary>
    /// <param name="changed">Whether the pass produced a different answer than was stored.</param>
    public bool Leave(bool changed)
    {
        if (changed)
            Volatile.Write(ref _changed, 1);

        int remaining = Interlocked.Decrement(ref _pending);

        if (Volatile.Read(ref _changed) == 0)
            return false;

        long now = _clock();
        if (remaining > 0 && now - Volatile.Read(ref _lastRefreshAt) < _intervalMs)
            return false;

        // Whoever takes the flag refreshes; a change that lands after the exchange is announced
        // by its own completion, which either settles the batch or waits for the interval.
        if (Interlocked.Exchange(ref _changed, 0) == 0)
            return false;

        Volatile.Write(ref _lastRefreshAt, now);
        return true;
    }

    /// <summary>How many passes are queued or running.</summary>
    internal int Pending => Volatile.Read(ref _pending);
}

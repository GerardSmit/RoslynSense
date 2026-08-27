namespace RoslynMCP.Lsp.Search;

/// <summary>
/// Lets the background index sweep have the machine while nobody is waiting on it, and take a
/// step back the moment somebody is.
/// </summary>
/// <remarks>
/// <para>
/// The sweep in <see cref="SolutionWarmup"/> ran at a fixed three-at-a-time, chosen so that a
/// keystroke arriving mid-sweep would still find a free core. That is the right ceiling while
/// someone is typing and the wrong one for the seconds before they are: on a cold open the sweep
/// is the only work in the process, and holding it to three cores means the caches it builds are
/// ready later than they had to be — which is time paid back with interest by whichever gesture
/// arrives before they exist.
/// </para>
/// <para>
/// A gate rather than a fixed degree of parallelism because the answer changes inside one sweep.
/// Idle admits every document; busy admits <see cref="Narrow"/>. The check is per document, so the
/// sweep gives way within one document's work of a search starting, and takes the machine back
/// within one of it finishing.
/// </para>
/// <para>
/// Racy by construction and deliberately so: a search that starts between a document's check and
/// its work meets one unthrottled document, which is milliseconds. Making that impossible would
/// mean the foreground waiting on the background to acknowledge it, which is the wait this exists
/// to prevent.
/// </para>
/// </remarks>
internal static class ForegroundGate
{
    /// <summary>What the sweep is allowed while a request is in flight — the ceiling the sweep
    /// used to hold to unconditionally.</summary>
    private const int Narrow = 3;

    private static int s_busy;
    private static readonly SemaphoreSlim s_narrow = new(Narrow, Narrow);

    /// <summary>Marks a request the user is waiting on. Disposed when it is answered.</summary>
    public static IDisposable Busy()
    {
        Interlocked.Increment(ref s_busy);
        return new Idle();
    }

    /// <summary>
    /// Admits one item of background work: immediately when nothing is in flight, behind the
    /// narrow gate when something is. The returned scope is null when nothing was taken.
    /// </summary>
    public static async ValueTask<IDisposable?> AdmitAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref s_busy) == 0)
            return null;

        await s_narrow.WaitAsync(ct).ConfigureAwait(false);
        return new Slot();
    }

    private sealed class Idle : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Decrement(ref s_busy);
        }
    }

    private sealed class Slot : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                s_narrow.Release();
        }
    }
}

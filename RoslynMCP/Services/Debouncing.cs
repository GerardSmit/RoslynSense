using System.Collections.Concurrent;

namespace RoslynMCP.Services;

/// <summary>
/// One pending action at a time: scheduling replaces whatever was still waiting to run.
/// </summary>
/// <remarks>
/// <para>
/// This class exists because the pattern it wraps was hand-rolled in eight places, and one of the
/// copies crashed the process. The dangerous part is not the debounce itself — it is the lifetime
/// of the <see cref="CancellationTokenSource"/> shared between the task that owns it and the next
/// caller that supersedes it. Get either of two lines wrong and the failure is an unhandled
/// <see cref="ObjectDisposedException"/> on a thread nobody guards (a
/// <see cref="FileSystemWatcher"/> callback, a thread-pool worker), which kills the process:
/// <list type="bullet">
/// <item>Reading <c>cts.Token</c> inside the scheduled task races the superseding caller's
/// dispose. The token must be captured before the source can escape.</item>
/// <item>Cancelling the superseded source races its own task's dispose-on-the-way-out. The
/// cancel must tolerate <see cref="ObjectDisposedException"/>.</item>
/// </list>
/// Both invariants live here so a ninth call site cannot re-introduce the bug by copying the
/// shape and forgetting one line.
/// </para>
/// <para>
/// Delay is per call rather than per instance because two of the real call sites compute it —
/// a coalescing ceiling that forces the flush through at <see cref="TimeSpan.Zero"/> once a
/// steady stream has held the quiet period off for too long.
/// </para>
/// <para>
/// The action runs on the thread pool, never on the caller's thread. An exception it lets out is
/// contained and logged rather than allowed to reach the pool: the callers are background
/// conveniences (a tree refresh, a cache warm), and none of them is worth the process.
/// </para>
/// </remarks>
internal sealed class Debouncer(string name)
{
    private readonly object _gate = new();
    private CancellationTokenSource? _pending;

    /// <summary>
    /// Schedules <paramref name="action"/> to run after <paramref name="delay"/>, replacing any
    /// previously scheduled run that has not started its action yet. The token passed to the
    /// action is cancelled when a newer call supersedes it or <see cref="Cancel"/> is called.
    /// </summary>
    /// <returns>The scheduled run, for tests that need to await it. Never faults.</returns>
    public Task Restart(TimeSpan delay, Func<CancellationToken, Task> action)
    {
        CancellationTokenSource cts;
        CancellationToken token;

        lock (_gate)
        {
            try { _pending?.Cancel(); }
            catch (ObjectDisposedException) { }

            cts = _pending = new CancellationTokenSource();
            token = cts.Token;
        }

        return Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, token);

                await action(token);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer call, or cancelled by the owner going away.
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{name}] Debounced work failed: {ex.Message}");
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_pending, cts))
                        _pending = null;
                }

                cts.Dispose();
            }
        });
    }

    /// <summary>Cancels the pending run, if any. For the owner's dispose path.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            try { _pending?.Cancel(); }
            catch (ObjectDisposedException) { }

            _pending = null;
        }
    }
}

/// <summary>
/// <see cref="Debouncer"/> with an independent slot per key — per file, per project — so a burst
/// on one key never delays or cancels the work of another.
/// </summary>
/// <remarks>
/// Same two lifetime invariants as <see cref="Debouncer"/>, owned here for the same reason. The
/// map only ever holds sources whose runs have not finished; each run removes its own entry on
/// the way out, so an idle instance holds nothing.
/// </remarks>
internal sealed class KeyedDebouncer(string name)
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Schedules <paramref name="action"/> for <paramref name="key"/> after
    /// <paramref name="delay"/>, replacing any run still pending on the same key.
    /// </summary>
    /// <returns>The scheduled run, for tests that need to await it. Never faults.</returns>
    public Task Restart(string key, TimeSpan delay, Func<CancellationToken, Task> action)
    {
        var cts = new CancellationTokenSource();
        var token = cts.Token;

        CancellationTokenSource? previous = null;
        _pending.AddOrUpdate(key, cts, (_, old) => { previous = old; return cts; });

        if (previous is not null)
        {
            try { previous.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        return Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, token);

                await action(token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{name}] Debounced work for '{key}' failed: {ex.Message}");
            }
            finally
            {
                ((ICollection<KeyValuePair<string, CancellationTokenSource>>)_pending)
                    .Remove(new KeyValuePair<string, CancellationTokenSource>(key, cts));
                cts.Dispose();
            }
        });
    }

    /// <summary>How many keys have a run pending or in flight right now.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>Cancels the pending run on one key, if any. True when there was one.</summary>
    public bool Cancel(string key)
    {
        if (!_pending.TryRemove(key, out var cts))
            return false;

        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }

        return true;
    }

    /// <summary>Cancels every pending run. For dispose paths and test resets.</summary>
    public void CancelAll()
    {
        foreach (var key in _pending.Keys.ToList())
            Cancel(key);
    }
}

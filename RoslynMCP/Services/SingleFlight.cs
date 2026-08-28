using System.Collections.Concurrent;

namespace RoslynMCP.Services;

/// <summary>
/// At most one run per key at a time: callers that ask for the same key while a run is in flight
/// join that run instead of starting another.
/// </summary>
/// <remarks>
/// <para>
/// The eviction rule is the whole of it, and it is the part that is easy to get subtly wrong. An
/// entry is removed when its <em>run</em> ends — never when a caller waiting on it gives up.
/// </para>
/// <para>
/// Removing it in a waiter's <c>finally</c> looks equivalent and is not. A waiter's wait ends early
/// whenever that caller's own cancellation token fires, which in an editor is the ordinary case
/// rather than the exceptional one: every request the next keystroke supersedes cancels one. The
/// entry was then dropped while the work behind it was still running, the next caller found nothing
/// in flight, and a second run started on top of the first — precisely what single-flighting exists
/// to prevent, and only under load, so it survives every quiet test.
/// </para>
/// <para>
/// For <see cref="RestoreService"/> that second run was a second MSBuild restore of the same
/// solution, writing the same <c>obj\project.assets.json</c> as the first. NuGet reports the
/// collision as "Cannot create a file when that file already exists", naming no file and no
/// project, on a solution that restores perfectly from a shell.
/// </para>
/// <para>
/// Nothing is remembered past the run: a completed entry is gone, so the next caller starts a fresh
/// one rather than joining a stale success — or a stale failure, which would otherwise poison the
/// key for the life of the process after a single transient blip.
/// </para>
/// </remarks>
internal sealed class SingleFlight
{
    private readonly ConcurrentDictionary<string, Task> _inflight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The run in flight for <paramref name="key"/>, starting one with <paramref name="run"/> if
    /// there is none. The returned task is shared by every caller, so awaiting it must be done with
    /// each caller's own token rather than by cancelling the task.
    /// </summary>
    public Task Start(string key, Func<string, Task> run)
    {
        var started = _inflight.GetOrAdd(key, run);

        // Keyed on the task identity, so a caller that has already started a *newer* run does not
        // have it dropped out from under it by this one's completion.
        //
        // Attaching once per caller rather than once per run is deliberate and harmless: removal is
        // idempotent, and by the time GetOrAdd has returned the entry is in the dictionary — so a
        // run that finished before its first caller could attach still evicts itself here.
        _ = started.ContinueWith(
            finished => _inflight.TryRemove(new KeyValuePair<string, Task>(key, finished)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return started;
    }

    /// <summary>Whether a run is in flight for <paramref name="key"/>. For tests.</summary>
    internal bool IsInFlight(string key) => _inflight.ContainsKey(key);
}

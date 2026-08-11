namespace RoslynMCP.Services;

/// <summary>A unit of long-running work the user should see feedback for.</summary>
public interface IProgressScope : IAsyncDisposable
{
    void Report(string message, int? percentage = null);
}

/// <summary>
/// Layer-safe progress reporting. Services announce work; whoever can render it installs a
/// factory. The LSP layer does that at session start (<c>$/progress</c>); with no editor
/// attached — an MCP-only process — everything falls through to a no-op, so callers never
/// need to know whether anyone is watching.
/// </summary>
public static class ProgressReporter
{
    private static readonly IProgressScope s_noop = new NoopScope();

    /// <summary>Installed by the LSP layer. Null means nothing can render progress.</summary>
    public static Func<string, CancellationToken, Task<IProgressScope>>? Factory { get; set; }

    public static async Task<IProgressScope> BeginAsync(string title, CancellationToken ct = default)
    {
        if (Factory is not { } factory)
            return s_noop;

        try { return await factory(title, ct); }
        catch { return s_noop; } // progress must never break the work it describes
    }

    /// <summary>
    /// A scope that only becomes visible if the work is still running after <paramref name="delay"/>.
    /// </summary>
    /// <remarks>
    /// For work that is usually instant and occasionally slow. <c>workspace/diagnostic</c> is the
    /// case this exists for: the editor re-pulls it after any change that could reach another file,
    /// so a notification on every pull reads as "it is reloading the solution again" even when the
    /// sweep answered "unchanged" for everything and returned in a few milliseconds. Announcing only
    /// the sweeps that actually take time keeps the signal meaning what the user thinks it means.
    /// </remarks>
    public static IProgressScope BeginDeferred(string title, TimeSpan delay, CancellationToken ct = default)
    {
        if (Factory is null)
            return s_noop;

        return new DeferredScope(title, delay, ct);
    }

    private sealed class NoopScope : IProgressScope
    {
        public void Report(string message, int? percentage = null) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DeferredScope : IProgressScope
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task<IProgressScope?> _pending;
        private readonly object _gate = new();
        private (string Message, int? Percentage)? _last;
        private IProgressScope? _live;

        public DeferredScope(string title, TimeSpan delay, CancellationToken ct)
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);
            _pending = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, linked.Token);
                    var scope = await BeginAsync(title, linked.Token);

                    // Whatever the work last said, so the notification does not appear blank.
                    (string, int?)? replay;
                    lock (_gate)
                    {
                        _live = scope;
                        replay = _last;
                    }
                    if (replay is { } r)
                        scope.Report(r.Item1, r.Item2);

                    return scope;
                }
                catch (OperationCanceledException)
                {
                    // Finished before it was worth mentioning.
                    return null;
                }
                finally
                {
                    linked.Dispose();
                }
            });
        }

        public void Report(string message, int? percentage = null)
        {
            IProgressScope? live;
            lock (_gate)
            {
                _last = (message, percentage);
                live = _live;
            }
            live?.Report(message, percentage);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                _cts.Cancel();

                // Awaited rather than abandoned: the scope may have been created in the instant
                // between the cancel and the check, and an unended progress token is a
                // notification the editor never takes down. A scope cancelled part-way through
                // creation ends whatever it had already begun before it propagates the
                // cancellation, so nothing is left open either way.
                var scope = await _pending.ConfigureAwait(false);
                if (scope is not null)
                    await scope.DisposeAsync();
            }
            catch (Exception ex)
            {
                // Ending progress must never fault the work it was describing.
                Console.Error.WriteLine($"[Progress] Ending deferred scope failed: {ex.Message}");
            }
            finally
            {
                _cts.Dispose();
            }
        }
    }
}

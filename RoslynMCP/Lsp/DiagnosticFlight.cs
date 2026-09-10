namespace RoslynMCP.Lsp;

/// <summary>Owned cancellation for work shared by diagnostic requests.</summary>
internal sealed class DiagnosticFlight<T>
{
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lifetimeGate = new();
    private Task? _cancellationCallbacks;
    private bool _disposed;

    public CancellationToken Token { get; }
    public Lazy<Task<T>> Work { get; }

    // These fields are protected by the owning cache's gate, including registration of
    // new waiters. An abandoned flight stays registered until completion so a retry cannot
    // overlap it; the retry waits for retirement instead of joining its canceled result.
    public int Waiters;
    public bool Abandoned;
    public bool Invalidated;
    public bool Completed;

    public DiagnosticFlight(Func<DiagnosticFlight<T>, Task<T>> compute)
    {
        Token = _stop.Token;
        Work = new(() => compute(this));
    }

    public void Cancel()
    {
        lock (_lifetimeGate)
        {
            if (!_disposed)
                _cancellationCallbacks ??= _stop.CancelAsync();
        }
    }

    public void Dispose()
    {
        Task? callbacks;
        lock (_lifetimeGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            callbacks = _cancellationCallbacks;
        }

        // CancelAsync marks the token immediately and invokes arbitrary analyzer callbacks
        // outside our locks. Do not race CTS disposal against those callbacks or block the
        // computation's completion waiting for them.
        if (callbacks is null || callbacks.IsCompletedSuccessfully)
            _stop.Dispose();
        else
            _ = DisposeAfterCancellationAsync(callbacks);
    }

    private async Task DisposeAfterCancellationAsync(Task callbacks)
    {
        try { await callbacks; }
        catch (Exception ex)
        {
            Services.ServiceLog.Warn($"A diagnostic cancellation callback failed: {ex.Message}",
                key: "diagnostic-cancellation-callback");
        }
        finally { _stop.Dispose(); }
    }
}

using System.Collections.Concurrent;
using RoslynMCP.Config;
using RoslynMCP.Lsp.Protocol;
using StreamJsonRpc;

namespace RoslynMCP.Lsp;

/// <summary>
/// Computes and pushes <c>textDocument/publishDiagnostics</c> for one LSP session.
/// Debounced per document: rapid didChange bursts collapse into one compute ~400ms after
/// the last keystroke; didOpen/didSave publish immediately.
/// Two phases per schedule — compiler diagnostics first so squiggles keep their current
/// latency, then analyzers after a longer idle, republished as the union. publishDiagnostics
/// replaces the whole set per URI, so phase two must re-send phase one's findings with it.
/// </summary>
internal sealed class DiagnosticsPublisher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan AnalyzerDebounce = TimeSpan.FromMilliseconds(1500);

    private readonly JsonRpc _rpc;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _disposed = new();

    public DiagnosticsPublisher(JsonRpc rpc) => _rpc = rpc;

    public void Schedule(string filePath, bool immediate)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_disposed.Token);
        var previous = _pending.Exchange(filePath, cts);
        // Cancel only — disposing here races the in-flight task still holding the token
        // (ObjectDisposedException inside Task.Delay); the GC reclaims cancelled sources.
        previous?.Cancel();

        _ = RunAsync(filePath, immediate, cts.Token);
    }

    public void Clear(string filePath)
    {
        if (_pending.TryRemove(filePath, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
        _ = PublishAsync(filePath, Array.Empty<Protocol.Diagnostic>(), _disposed.Token);
    }

    private async Task RunAsync(string filePath, bool immediate, CancellationToken ct)
    {
        try
        {
            if (!immediate)
                await Task.Delay(Debounce, ct);

            var diagnostics = await Handlers.DiagnosticsHandler.ComputeAsync(filePath, ct);

            ct.ThrowIfCancellationRequested();
            await PublishAsync(filePath, diagnostics, ct);

            if (LspFeatureOptions.AnalyzerDiagnostics)
                await RunAnalyzerPhaseAsync(filePath, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Lsp] Diagnostics for '{filePath}' failed: {ex.Message}");
        }
    }

    /// <summary>Phase two. Cancelled outright by the next keystroke, which is the point:
    /// analyzers only run once the user pauses.</summary>
    private async Task RunAnalyzerPhaseAsync(string filePath, CancellationToken ct)
    {
        await Task.Delay(AnalyzerDebounce, ct);

        var merged = await Handlers.DiagnosticsHandler.ComputeWithAnalyzersAsync(filePath, ct);

        ct.ThrowIfCancellationRequested();
        await PublishAsync(filePath, merged, ct);
    }

    private Task PublishAsync(string filePath, Protocol.Diagnostic[] diagnostics, CancellationToken ct)
    {
        var payload = new PublishDiagnosticsParams(LspConverters.PathToUri(filePath), null, diagnostics);
        return _rpc.NotifyWithParameterObjectAsync("textDocument/publishDiagnostics", payload);
    }

    public void Dispose()
    {
        _disposed.Cancel();
        foreach (var cts in _pending.Values)
            cts.Dispose();
        _pending.Clear();
        _disposed.Dispose();
    }
}

file static class ConcurrentDictionaryExtensions
{
    /// <summary>Atomically swaps the value for a key, returning the previous value (or null).</summary>
    public static CancellationTokenSource? Exchange(
        this ConcurrentDictionary<string, CancellationTokenSource> dict,
        string key, CancellationTokenSource value)
    {
        CancellationTokenSource? previous = null;
        dict.AddOrUpdate(key, value, (_, old) => { previous = old; return value; });
        return previous;
    }
}

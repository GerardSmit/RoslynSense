using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using RoslynMCP.Lsp.Protocol;
using StreamJsonRpc;

namespace RoslynMCP.Lsp;

/// <summary>
/// Computes and pushes <c>textDocument/publishDiagnostics</c> for one LSP session.
/// Debounced per document: rapid didChange bursts collapse into one compute ~400ms after
/// the last keystroke; didOpen/didSave publish immediately.
/// </summary>
internal sealed class DiagnosticsPublisher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);

    private readonly JsonRpc _rpc;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _disposed = new();

    public DiagnosticsPublisher(JsonRpc rpc) => _rpc = rpc;

    public void Schedule(string filePath, bool immediate)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_disposed.Token);
        var previous = _pending.Exchange(filePath, cts);
        previous?.Cancel();
        previous?.Dispose();

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

            var document = await LspDocumentResolver.ResolveAsync(filePath, ct);
            if (document is null)
                return;

            var model = await document.GetSemanticModelAsync(ct);
            if (model is null)
                return;

            var diagnostics = model.GetDiagnostics(cancellationToken: ct)
                .Where(d => d.Severity != DiagnosticSeverity.Hidden && d.Location.IsInSource)
                .Select(d => new Protocol.Diagnostic(
                    LspConverters.ToRange(d.Location.GetLineSpan().Span),
                    LspConverters.ToLspSeverity(d.Severity),
                    d.Id,
                    "roslyn-sense",
                    d.GetMessage()))
                .ToArray();

            ct.ThrowIfCancellationRequested();
            await PublishAsync(filePath, diagnostics, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Lsp] Diagnostics for '{filePath}' failed: {ex.Message}");
        }
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

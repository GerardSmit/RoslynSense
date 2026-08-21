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
    private readonly Services.KeyedDebouncer _debounce = new("Lsp");
    private readonly CancellationTokenSource _disposed = new();

    public DiagnosticsPublisher(JsonRpc rpc) => _rpc = rpc;

    /// <summary>
    /// The session's enabled languages, once <c>initialize</c> has read them. Settable rather than
    /// a constructor argument because the publisher is created when the connection is attached,
    /// which is before the client has told us which languages it wants. Until then it is null and
    /// the handlers fall back to the registration gate — the same rule every other pre-initialize
    /// path follows.
    /// </summary>
    public Languages.LanguageSession? Languages { get; set; }

    public void Schedule(string filePath, bool immediate) =>
        _debounce.Restart(filePath, immediate ? TimeSpan.Zero : Debounce, async ct =>
        {
            // The session can detach between a schedule and its run; a disposed publisher must
            // not push into a connection that is being torn down.
            if (_disposed.IsCancellationRequested)
                return;

            var diagnostics = await Handlers.DiagnosticsHandler.ComputeAsync(filePath, ct, Languages);

            ct.ThrowIfCancellationRequested();
            await PublishAsync(filePath, diagnostics, ct);

            if (LspFeatureOptions.AnalyzerDiagnostics)
                await RunAnalyzerPhaseAsync(filePath, ct);
        });

    public void Clear(string filePath)
    {
        _debounce.Cancel(filePath);
        _ = PublishAsync(filePath, Array.Empty<Protocol.Diagnostic>(), _disposed.Token);
    }

    /// <summary>Phase two. Cancelled outright by the next keystroke, which is the point:
    /// analyzers only run once the user pauses.</summary>
    private async Task RunAnalyzerPhaseAsync(string filePath, CancellationToken ct)
    {
        await Task.Delay(AnalyzerDebounce, ct);

        var merged = await Handlers.DiagnosticsHandler.ComputeWithAnalyzersAsync(filePath, ct, Languages);

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
        _debounce.CancelAll();
        _disposed.Dispose();
    }
}

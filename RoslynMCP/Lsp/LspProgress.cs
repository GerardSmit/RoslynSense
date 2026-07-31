using System.Text.Json.Serialization;
using RoslynMCP.Services;
using StreamJsonRpc;

namespace RoslynMCP.Lsp;

/// <summary>
/// Server-initiated work-done progress (LSP <c>window/workDoneProgress/create</c> +
/// <c>$/progress</c>). Solution loads, restores, and reloads take seconds to minutes; without
/// this the editor shows nothing at all and a cold open is indistinguishable from a hang.
/// </summary>
internal static class LspProgress
{
    /// <summary>Installs this as the process-wide progress renderer. Idempotent.</summary>
    public static void Install() =>
        ProgressReporter.Factory = async (title, ct) => await BeginAsync(title, ct);

    private static async Task<IProgressScope> BeginAsync(string title, CancellationToken ct)
    {
        var sessions = LspSessionRegistry.ActiveSessions();
        if (sessions.Count == 0)
            return new NoopScope();

        string token = $"roslyn-sense/{Guid.NewGuid():N}";
        var live = new List<JsonRpc>(sessions.Count);

        foreach (var rpc in sessions)
        {
            try
            {
                // The client must create the token before any $/progress for it is valid.
                await rpc.InvokeWithParameterObjectAsync<object?>(
                    "window/workDoneProgress/create", new WorkDoneProgressCreateParams(token), ct);
                await rpc.NotifyWithParameterObjectAsync("$/progress",
                    new ProgressParams(token, WorkDoneProgress.Begin(title)));
                live.Add(rpc);
            }
            catch (Exception ex) when (ex is RemoteInvocationException or ConnectionLostException or ObjectDisposedException)
            {
                // Client can't or won't render progress — carry on silently.
            }
        }

        return live.Count == 0 ? new NoopScope() : new Scope(token, live);
    }

    private sealed class Scope(string token, List<JsonRpc> sessions) : IProgressScope
    {
        public void Report(string message, int? percentage = null) =>
            Send(WorkDoneProgress.Report(message, percentage));

        public ValueTask DisposeAsync()
        {
            Send(WorkDoneProgress.End());
            return ValueTask.CompletedTask;
        }

        private void Send(WorkDoneProgress value)
        {
            foreach (var rpc in sessions)
            {
                try { _ = rpc.NotifyWithParameterObjectAsync("$/progress", new ProgressParams(token, value)); }
                catch (Exception ex) when (ex is ConnectionLostException or ObjectDisposedException)
                {
                    // Session ended mid-operation.
                }
            }
        }
    }

    private sealed class NoopScope : IProgressScope
    {
        public void Report(string message, int? percentage = null) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed record WorkDoneProgressCreateParams(
    [property: JsonPropertyName("token")] string Token);

public sealed record ProgressParams(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("value")] WorkDoneProgress Value);

public sealed record WorkDoneProgress(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("message")] string? Message = null,
    [property: JsonPropertyName("percentage")] int? Percentage = null,
    [property: JsonPropertyName("cancellable")] bool Cancellable = false)
{
    public static WorkDoneProgress Begin(string title) => new("begin", Title: title);
    public static WorkDoneProgress Report(string message, int? percentage) =>
        new("report", Message: message, Percentage: percentage);
    public static WorkDoneProgress End() => new("end");
}

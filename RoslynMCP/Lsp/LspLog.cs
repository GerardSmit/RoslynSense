using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using RoslynMCP.Services;
using StreamJsonRpc;

namespace RoslynMCP.Lsp;

/// <summary>
/// Server-side failures the user should know about. Everything goes to
/// <c>window/logMessage</c> (the editor's output channel); warnings and errors additionally
/// raise <c>window/showMessage</c>, rate-limited per message key so a failure that repeats on
/// every request cannot turn into a stream of toasts.
///
/// Before this existed, a failed project load or a crashed analyzer went to stderr, where the
/// user never saw it — the visible symptom was "nothing works" with no explanation.
/// </summary>
internal static class LspLog
{
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, DateTime> s_lastShown = new();

    public static void Info(string message) => Send(MessageType.Info, message, null);

    public static void Warn(string message, string? key = null) => Send(MessageType.Warning, message, key);

    public static void Error(string message, string? key = null) => Send(MessageType.Error, message, key);

    /// <summary>Installs the sink so non-LSP services can report through the editor.</summary>
    public static void Install() => ServiceLog.Sink = (severity, message, key) =>
    {
        if (severity == ServiceLogSeverity.Debug)
        {
            SendDebug(message);
            return;
        }
        Send(severity switch
        {
            ServiceLogSeverity.Error => MessageType.Error,
            ServiceLogSeverity.Warning => MessageType.Warning,
            _ => MessageType.Info,
        }, message, key);
    };

    /// <summary>
    /// Self-diagnostics go to the extension's own debug output channel via a custom
    /// notification, not through <c>window/logMessage</c>: the standard channel is the user's
    /// view of their solution, and a report about the tool's internals showing up there — let
    /// alone as a toast — is how the sweep-convergence telemetry became a complaint.
    /// </summary>
    private static void SendDebug(string message)
    {
        Console.Error.WriteLine($"[Lsp] Debug: {message}");

        var payload = new DebugLogParams(message);
        foreach (var rpc in LspSessionRegistry.ActiveSessions())
        {
            try
            {
                _ = rpc.NotifyWithParameterObjectAsync("roslynSense/debugLog", payload);
            }
            catch (Exception ex) when (ex is ConnectionLostException or ObjectDisposedException)
            {
                // Session ended; the others still get it.
            }
        }
    }

    private static void Send(MessageType type, string message, string? key)
    {
        // stderr stays the daemon's own record — it is what a bug report can attach.
        Console.Error.WriteLine($"[Lsp] {type}: {message}");

        var sessions = LspSessionRegistry.ActiveSessions();
        if (sessions.Count == 0)
            return;

        var payload = new ShowMessageParams((int)type, message);
        bool show = type is MessageType.Error or MessageType.Warning && ShouldShow(key ?? message);

        foreach (var rpc in sessions)
        {
            try
            {
                _ = rpc.NotifyWithParameterObjectAsync("window/logMessage", payload);
                if (show)
                    _ = rpc.NotifyWithParameterObjectAsync("window/showMessage", payload);
            }
            catch (Exception ex) when (ex is ConnectionLostException or ObjectDisposedException)
            {
                // Session ended; the others still get it.
            }
        }
    }

    private static bool ShouldShow(string key)
    {
        var now = DateTime.UtcNow;
        var last = s_lastShown.GetOrAdd(key, DateTime.MinValue);
        if (now - last < RepeatWindow)
            return false;

        s_lastShown[key] = now;
        return true;
    }

    private enum MessageType
    {
        Error = 1,
        Warning = 2,
        Info = 3,
        Log = 4,
    }
}

public sealed record ShowMessageParams(
    [property: JsonPropertyName("type")] int Type,
    [property: JsonPropertyName("message")] string Message);

/// <summary>A line for the extension's debug output channel.</summary>
public sealed record DebugLogParams(
    [property: JsonPropertyName("message")] string Message);

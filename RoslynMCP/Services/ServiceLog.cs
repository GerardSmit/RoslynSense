namespace RoslynMCP.Services;

public enum ServiceLogSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Layer-safe reporting of failures that a user should see. Services call this; the LSP layer
/// installs a sink that forwards to the editor. With no sink (an MCP-only process) messages
/// still reach stderr, which is where they went before.
/// </summary>
public static class ServiceLog
{
    /// <summary>Installed by the LSP layer. The key groups repeats for rate limiting.</summary>
    public static Action<ServiceLogSeverity, string, string?>? Sink { get; set; }

    public static void Info(string message) => Report(ServiceLogSeverity.Info, message, null);

    public static void Warn(string message, string? key = null) =>
        Report(ServiceLogSeverity.Warning, message, key);

    public static void Error(string message, string? key = null) =>
        Report(ServiceLogSeverity.Error, message, key);

    private static void Report(ServiceLogSeverity severity, string message, string? key)
    {
        if (Sink is { } sink)
        {
            try { sink(severity, message, key); return; }
            catch { /* reporting must never break the caller */ }
        }
        Console.Error.WriteLine($"[{severity}] {message}");
    }
}

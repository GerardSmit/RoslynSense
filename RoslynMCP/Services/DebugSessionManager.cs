namespace RoslynMCP.Services;

/// <summary>
/// Manages the singleton debug session. Only one debug session can be active at a time.
/// </summary>
internal static class DebugSessionManager
{
    private static DebuggerService? s_session;
    private static readonly object s_lock = new();

    public static DebuggerService? GetSession()
    {
        lock (s_lock)
        {
            return s_session;
        }
    }

    public static DebuggerService CreateSession()
    {
        lock (s_lock)
        {
            s_session?.Dispose();
            s_session = new DebuggerService();
            return s_session;
        }
    }

    public static void DisposeSession()
    {
        lock (s_lock)
        {
            s_session?.Stop();
            s_session?.Dispose();
            s_session = null;
        }
    }
}

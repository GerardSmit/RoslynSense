namespace RoslynMCP.Services;

/// <summary>
/// Manages the singleton debug session. Only one debug session can be active at a time.
/// </summary>
/// <remarks>
/// Also picks the engine, which the caller never selects by hand: netcoredbg cannot attach to
/// .NET Framework and ICorDebug is the only thing that can, so choosing wrong just fails. The
/// runtime is inferred from the project being debugged, or from the target process when attaching.
/// </remarks>
internal static class DebugSessionManager
{
    private static IDebugBackend? s_session;
    private static readonly Lock s_lock = new();

    public static IDebugBackend? GetSession()
    {
        lock (s_lock)
        {
            return s_session;
        }
    }

    /// <summary>Creates a session for a project, selecting the engine from its target framework.</summary>
    public static IDebugBackend CreateSessionForProject(string projectPath) =>
        CreateSession(DebugRuntimeDetector.ForProject(projectPath));

    /// <summary>Creates a session for a running process, selecting the engine from its loaded CLR.</summary>
    public static IDebugBackend CreateSessionForProcess(int pid) =>
        CreateSession(DebugRuntimeDetector.ForProcess(pid));

    public static IDebugBackend CreateSession(DebugRuntime runtime)
    {
        lock (s_lock)
        {
            s_session?.Dispose();
            s_session = runtime == DebugRuntime.NetFramework
                ? new IcorDebugBackend()
                : new DebuggerService();
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

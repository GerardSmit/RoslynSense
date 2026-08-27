using RoslynMCP.Services.Debugging;

namespace RoslynMCP.Services;

/// <summary>
/// Manages the singleton debug session. Only one debug session can be active at a time.
/// </summary>
/// <remarks>
/// Also picks the engine, which the caller never selects by hand: netcoredbg cannot attach to
/// .NET Framework and ICorDebug is the only thing that can, so choosing wrong just fails. The
/// runtime is inferred from the project being debugged, or from the target process when attaching.
/// A CoreCLR target is the one case where both engines can do the job, and
/// <see cref="Config.DebugEngineOptions"/> says which of them gets it.
/// Sessions are wrapped so their state is mirrored to <see cref="DebugStateStore"/> and
/// controllable from the editor via <see cref="DebugCommandPipeServer"/>.
/// </remarks>
internal static class DebugSessionManager
{
    private static IDebugBackend? s_session;
    private static DebugCommandPipeServer? s_pipeServer;
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
            s_session = new PublishingDebugBackend(EngineFor(runtime));
            s_pipeServer ??= new DebugCommandPipeServer(GetSession);
            return s_session;
        }
    }

    /// <summary>
    /// The engine a target of this runtime is debugged with.
    /// </summary>
    /// <remarks>
    /// .NET Framework has no choice. CoreCLR has two, and the setting decides — read here, when
    /// the session is created, rather than held by the backend, so a change reaches the next
    /// session without anything having to notice it changed.
    /// </remarks>
    private static IDebugBackend EngineFor(DebugRuntime runtime)
    {
        if (runtime == DebugRuntime.NetFramework)
            return new IcorDebugBackend(Debugger.DebugRuntime.NetFramework);

        return Config.DebugEngineOptions.CoreClr == Config.CoreClrDebugEngine.IcorDebug
            ? new IcorDebugBackend(Debugger.DebugRuntime.CoreClr)
            : new DebuggerService();
    }

    public static void DisposeSession()
    {
        lock (s_lock)
        {
            s_session?.Stop();
            s_session?.Dispose();
            s_session = null;
            s_pipeServer?.Dispose();
            s_pipeServer = null;
        }
    }
}

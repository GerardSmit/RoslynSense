namespace RoslynMCP.Services;

/// <summary>
/// The debug-session surface the <c>Debug*</c> tools drive, independent of which engine backs it.
/// </summary>
/// <remarks>
/// Two implementations exist because no single engine covers both runtimes:
/// <see cref="DebuggerService"/> drives netcoredbg over its MI protocol, which handles CoreCLR
/// only, and <see cref="IcorDebugBackend"/> drives ICorDebug, which is the only way to debug
/// .NET Framework. The tools are identical either way; only the routing in
/// <see cref="DebugSessionManager"/> differs.
/// </remarks>
internal interface IDebugBackend : IDisposable
{
    /// <summary>Where execution is currently suspended, or <c>null</c> when running.</summary>
    DebuggerService.StoppedFrame? CurrentFrame { get; }

    Task<string> StartTestSessionAsync(
        string csprojPath,
        string? filter,
        IEnumerable<(string file, int line)>? initialBreakpoints = null,
        CancellationToken cancellationToken = default);

    Task<string> AttachToProcessAsync(
        int pid,
        IEnumerable<(string file, int line)>? initialBreakpoints = null,
        CancellationToken cancellationToken = default);

    Task<(string Message, int? BreakpointId)> SetBreakpointAsync(
        string filePath, int line, string? condition = null, CancellationToken cancellationToken = default);

    Task<string> RemoveBreakpointAsync(int breakpointId, CancellationToken cancellationToken = default);

    Task<string> ContinueAsync(CancellationToken cancellationToken = default);
    Task<string> StepInAsync(CancellationToken cancellationToken = default);
    Task<string> StepOverAsync(CancellationToken cancellationToken = default);
    Task<string> StepOutAsync(CancellationToken cancellationToken = default);

    Task<string> EvaluateAsync(string expression, CancellationToken cancellationToken = default);
    Task<string> GetLocalsAsync(CancellationToken cancellationToken = default);
    Task<string> GetStackTraceAsync(CancellationToken cancellationToken = default);

    string GetStatus();
    string Stop();
}

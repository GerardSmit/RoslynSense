using RoslynMCP.Services.Debugging;

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

    /// <summary>
    /// Sets a source breakpoint.
    /// </summary>
    /// <param name="hitCondition">A hit-count rule (<c>&gt;= 3</c>, <c>= 3</c>, <c>% 5</c>).</param>
    /// <param name="logMessage">A message to log instead of stopping, with <c>{expression}</c>
    /// placeholders evaluated at the stop.</param>
    /// <remarks>
    /// Neither engine supports the last two arguments — netcoredbg advertises neither
    /// <c>supportsHitConditionalBreakpoints</c> nor <c>supportsLogPoints</c> — so they are
    /// recorded here and enforced by <see cref="Debugging.PublishingDebugBackend"/>, which sees
    /// every stop and can auto-resume through the ones that should not surface.
    /// </remarks>
    Task<(string Message, int? BreakpointId)> SetBreakpointAsync(
        string filePath, int line, string? condition = null, string? hitCondition = null,
        string? logMessage = null, CancellationToken cancellationToken = default);

    Task<string> RemoveBreakpointAsync(int breakpointId, CancellationToken cancellationToken = default);

    Task<string> ContinueAsync(CancellationToken cancellationToken = default);
    Task<string> StepInAsync(CancellationToken cancellationToken = default);
    Task<string> StepOverAsync(CancellationToken cancellationToken = default);
    Task<string> StepOutAsync(CancellationToken cancellationToken = default);

    Task<string> EvaluateAsync(string expression, CancellationToken cancellationToken = default);
    Task<string> GetLocalsAsync(CancellationToken cancellationToken = default);
    Task<string> GetStackTraceAsync(CancellationToken cancellationToken = default);

    /// <summary>Suspends a running target. The DAP <c>pause</c> button, and the only way out of a
    /// loop that never reaches a breakpoint.</summary>
    Task<string> InterruptAsync(CancellationToken cancellationToken = default);

    // --- Structured views ---
    //
    // The string members above format for the AI's markdown surface; these return the same data
    // as records so the editor's Variables and Call Stack views get real file paths, types, and
    // expandable objects instead of a regex reading formatted text back apart.

    Task<IReadOnlyList<StackFrameInfo>> GetStackFramesAsync(CancellationToken cancellationToken = default);

    /// <summary>Arguments and locals of <paramref name="frameId"/> (0 = innermost).</summary>
    Task<IReadOnlyList<VariableInfo>> GetVariablesAsync(
        int frameId, CancellationToken cancellationToken = default);

    /// <summary>Expands one value, addressed by a <see cref="VariableInfo.VariablesReference"/>
    /// handed out earlier in the same stop.</summary>
    Task<IReadOnlyList<VariableInfo>> GetVariableChildrenAsync(
        int variablesReference, CancellationToken cancellationToken = default);

    /// <summary>Assigns to a variable or member path. Returns the value as the target reports it
    /// back, which is not always what was written (narrowing, property setters).</summary>
    Task<(bool Ok, string Value, string Error)> SetVariableAsync(
        string name, string value, int frameId = 0, CancellationToken cancellationToken = default);

    /// <summary>
    /// Selects the frame that <see cref="EvaluateAsync"/> and <see cref="GetLocalsAsync"/> read
    /// from — walking up the stack to inspect a caller's state is otherwise impossible.
    /// </summary>
    Task<string> SelectFrameAsync(int frameId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ThreadInfo>> GetThreadsAsync(CancellationToken cancellationToken = default);

    /// <summary>The exception that caused the current stop, or <c>null</c> when the stop was not
    /// an exception.</summary>
    Task<ExceptionDetail?> GetExceptionInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>Selects which exceptions suspend the target.</summary>
    Task<string> SetExceptionFiltersAsync(
        ExceptionFilters filters, CancellationToken cancellationToken = default);

    string GetStatus();
    string Stop();
}

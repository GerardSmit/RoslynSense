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

    /// <summary>
    /// The debuggee's PID once the session has one, or <c>null</c> before that.
    /// </summary>
    /// <remarks>
    /// A launch is the case that needs this: attach was given the PID, but a launched debuggee's
    /// only exists after the engine starts it, and the DAP client has to be told which process it
    /// is now debugging.
    /// </remarks>
    int? DebuggeePid => null;

    /// <summary>
    /// Applies a changed debugger view policy — display strings, type proxies, browsable states,
    /// Just My Code — to a session that is already running.
    /// </summary>
    /// <remarks>
    /// Defaulted to a no-op because it only means something to the ICorDebug engine, which
    /// implements these attributes itself; netcoredbg applies its own and is told what it can be
    /// told when the session starts.
    /// </remarks>
    void ApplyViewOptions(RoslynMCP.Debugger.DebugDisplayOptions options) { }

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

    /// <summary>
    /// Runs to a source line without leaving a breakpoint behind — "Run to Cursor".
    /// </summary>
    /// <remarks>
    /// Distinct from setting a breakpoint and continuing: this one is gone once it fires, so the
    /// next lap round a loop does not stop again.
    /// </remarks>
    Task<string> RunToLocationAsync(
        string filePath, int line, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the instruction pointer within the current frame — "Set Next Statement".
    /// </summary>
    /// <remarks>
    /// The one debugger operation that changes what happens rather than observing it: re-run a
    /// block after fixing a variable, or step over a call that is about to throw.
    /// </remarks>
    Task<string> SetNextStatementAsync(
        string filePath, int line, CancellationToken cancellationToken = default);

    /// <summary>Loaded modules and whether each has symbols — the actionable answer to "my
    /// breakpoint never binds".</summary>
    Task<IReadOnlyList<ModuleInfo>> GetModulesAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops debugging but leaves the target running, for a process that was only being
    /// inspected and should not die with the session.</summary>
    Task<string> DetachAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends the session by letting the debuggee shut itself down, terminating it only if that
    /// runs past <paramref name="timeout"/>.
    /// </summary>
    /// <remarks>
    /// What <see cref="Stop"/> costs, and why this is not it: stopping kills the process where it
    /// stands, so hosted services never get <c>StopAsync</c>, <c>finally</c> blocks never run and
    /// nothing gets flushed. Only an engine that launched the debuggee itself can ask it to leave
    /// politely, so the default here is the old behaviour and the ICorDebug backend overrides it.
    /// </remarks>
    /// <returns>Whether the debuggee exited on its own, and a message describing how it ended.</returns>
    Task<(bool Graceful, string Message)> ShutdownAsync(
        TimeSpan timeout, CancellationToken cancellationToken = default)
        => Task.FromResult((false, Stop()));

    string GetStatus();
    string Stop();
}

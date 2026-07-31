using System.Threading.Channels;

namespace RoslynMCP.Debugger;

/// <summary>
/// Runs <see cref="DebugSession"/> directly, used when the target's bitness matches this process.
/// </summary>
public sealed class InProcessDebugEngine(uint sessionId) : IDebugEngine
{
    private readonly DebugSession _session = new(sessionId);

    public ChannelReader<DebugEvent> Events => _session.Events;

    public void Attach(int pid, IEnumerable<BreakpointSpec> breakpoints, DebugRuntime runtime) =>
        _session.Attach(pid, breakpoints, runtime);

    public void Launch(
        string executable, IReadOnlyList<string> arguments, IEnumerable<BreakpointSpec> breakpoints,
        IReadOnlyDictionary<string, string>? environment, string? workingDirectory,
        DebugRuntime runtime) =>
        _session.Launch(executable, arguments, breakpoints, environment, workingDirectory, runtime);

    public void AddBreakpoint(BreakpointSpec spec) => _session.AddBreakpoint(spec);

    public bool RemoveBreakpoint(string filePath, int line) => _session.RemoveBreakpoint(filePath, line);

    public void Continue() => _session.Continue();

    public void Pause() => _session.Pause();

    public void Step(StepKind kind) => _session.Step(kind);

    public Task<List<StackFrame>> StackTraceAsync() => _session.StackTraceAsync();

    public Task<List<DebugVariable>> VariablesAsync(uint frameIndex) => _session.VariablesAsync(frameIndex);

    public Task<(bool Ok, string Value, string Error)> EvaluateAsync(uint frameIndex, string expression) =>
        _session.EvaluateAsync(frameIndex, expression);

    public Task<(bool Ok, DebugVariable? Variable, string Error)> SetVariableAsync(
        uint frameIndex, string name, string value) =>
        _session.SetVariableAsync(frameIndex, name, value);

    /// <summary>
    /// Refused in-process: <c>ICorDebugModule2::ApplyChanges</c> does not validate the delta it is
    /// handed, and a malformed one faults inside the CLR rather than returning a failing HRESULT.
    /// </summary>
    /// <remarks>
    /// Observed, not theorised. Applying a hand-built delta took the whole host down with an
    /// access violation inside <c>ApplyChanges</c> — no managed exception, nothing catchable, the
    /// process simply gone. In the tool that host is the editor's language server and every other
    /// chat's workspace, which is far too much to stake on a metadata blob being well-formed.
    /// <see cref="WorkerDebugEngine"/> runs the same call in a separate process, where the blast
    /// radius is one disposable worker, so that is the only path allowed to make it.
    /// </remarks>
    public Task<(bool Ok, string Error)> ApplyDeltaAsync(
        string assemblyName, byte[] metadata, byte[] il, byte[] pdb) =>
        Task.FromResult((false,
            "Hot reload on .NET Framework is applied out of process. ICorDebug's ApplyChanges " +
            "faults on a malformed delta instead of failing, which would take this process with " +
            "it, so the in-process engine refuses it."));

    public Task<RunToLocationResponse> RunToLocationAsync(RunToLocationRequest request) =>
        _session.RunToLocationAsync(request);

    public Task<SetNextStatementResponse> SetNextStatementAsync(SetNextStatementRequest request) =>
        _session.SetNextStatementAsync(request);

    public Task<List<DebugModule>> ModulesAsync() => _session.ModulesAsync();

    public Task<(bool Ok, string Error)> DetachAsync() => _session.DetachAsync();

    public void SetExceptionPolicy(bool breakOnFirstChance) =>
        _session.SetExceptionPolicy(breakOnFirstChance);

    public void Terminate() => _session.Terminate();

    public void Dispose() => _session.Terminate();
}

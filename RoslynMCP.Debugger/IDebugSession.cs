using System.Threading.Channels;

namespace RoslynMCP.Debugger;

/// <summary>
/// The complete engine contract implemented by <see cref="DebugSession"/>, including capabilities
/// no caller drives yet (Edit and Continue, detach, threads, scopes, run-to-location,
/// set-next-statement). Debuggee console output arrives as <see cref="DebugEventKind.Output"/>
/// events on the single event stream.
/// </summary>
/// <remarks>
/// Distinct from <see cref="IDebugEngine"/>, which is the smaller subset the MCP tools actually
/// use and which a bitness-matched worker forwards. This interface exists to keep the full surface
/// from quietly regressing as the engine is maintained; widen <see cref="IDebugEngine"/> when a
/// tool needs more of it.
/// </remarks>
public interface IDebugSession
{
    uint Id { get; }
    int Pid { get; }
    ChannelReader<DebugEvent> Events { get; }

    void Launch(
        string executable, IReadOnlyList<string> args, IEnumerable<BreakpointSpec> breakpoints,
        IReadOnlyDictionary<string, string>? env = null, string? workingDirectory = null,
        DebugRuntime runtime = DebugRuntime.NetFramework);
    void Attach(
        int pid,
        IEnumerable<BreakpointSpec> breakpoints,
        DebugRuntime runtime = DebugRuntime.NetFramework);
    void AddBreakpoint(BreakpointSpec spec);
    bool RemoveBreakpoint(string filePath, int line);
    Task<BreakpointLocationsResponse> BreakpointLocationsAsync(BreakpointLocationsRequest request);
    Task<RunToLocationResponse> RunToLocationAsync(RunToLocationRequest request);
    Task<SetNextStatementResponse> SetNextStatementAsync(SetNextStatementRequest request);
    void Continue();
    void Pause();
    void Step(StepKind kind);
    void SetExceptionPolicy(bool breakOnFirstChance);
    Task<List<StackFrame>> StackTraceAsync();
    Task<List<DebugThread>> ThreadsAsync();
    Task<List<DebugModule>> ModulesAsync();
    Task<List<DebugVariable>> VariablesAsync(uint frameIndex);
    Task<List<DebugScope>> ScopesAsync(uint frameIndex);
    Task<(bool Ok, DebugVariable? Variable, string Error)> SetVariableAsync(uint frameIndex, string name, string value);
    Task<(bool Ok, string Value, string Error)> EvaluateAsync(uint frameIndex, string expression);
    Task<(bool Ok, string Error)> ApplyDeltaAsync(
        string assemblyName, byte[] metadata, byte[] il, byte[] pdb);
    Task<(bool Ok, string Error)> DetachAsync();
    void Terminate();
}

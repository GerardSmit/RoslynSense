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

    public void AddBreakpoint(BreakpointSpec spec) => _session.AddBreakpoint(spec);

    public bool RemoveBreakpoint(string filePath, int line) => _session.RemoveBreakpoint(filePath, line);

    public void Continue() => _session.Continue();

    public void Step(StepKind kind) => _session.Step(kind);

    public Task<List<StackFrame>> StackTraceAsync() => _session.StackTraceAsync();

    public Task<List<DebugVariable>> VariablesAsync(uint frameIndex) => _session.VariablesAsync(frameIndex);

    public Task<(bool Ok, string Value, string Error)> EvaluateAsync(uint frameIndex, string expression) =>
        _session.EvaluateAsync(frameIndex, expression);

    public Task<(bool Ok, DebugVariable? Variable, string Error)> SetVariableAsync(
        uint frameIndex, string name, string value) =>
        _session.SetVariableAsync(frameIndex, name, value);

    public void Terminate() => _session.Terminate();

    public void Dispose() => _session.Terminate();
}

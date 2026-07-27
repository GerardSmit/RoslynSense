using System.Collections.Concurrent;
using System.Text;
using RoslynMCP.Debugger;
using DebuggerEngine = RoslynMCP.Debugger.DebugSession;
using EngineRuntime = RoslynMCP.Debugger.DebugRuntime;

namespace RoslynMCP.Services;

/// <summary>
/// Backs the <c>Debug*</c> tools with the ICorDebug engine, which is the only way to debug
/// .NET Framework targets (netcoredbg speaks to CoreCLR only).
/// </summary>
/// <remarks>
/// Adapts the engine's event-stream shape to the request/response shape the tools expect: the
/// engine reports stops asynchronously, so this waits for the next stop after each resuming
/// command rather than returning while the target is still running.
/// </remarks>
internal sealed class IcorDebugBackend : IDebugBackend
{
    /// <summary>How long to wait for the target to stop again after a resume.</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);

    private IDebugEngine? _engine;
    private readonly ConcurrentDictionary<int, DebuggerService.BreakpointInfo> _breakpoints = new();
    private readonly ConcurrentQueue<string> _output = new();
    private readonly SemaphoreSlim _stopped = new(0);
    private readonly Lock _gate = new();

    private DebuggerService.StoppedFrame? _currentFrame;
    private DebuggerService.DebugState _state = DebuggerService.DebugState.NotStarted;
    private Task? _pump;
    private int _nextBreakpointId = 1;
    private bool _exited;

    public DebuggerService.StoppedFrame? CurrentFrame
    {
        get { lock (_gate) return _currentFrame; }
    }

    /// <summary>
    /// The engine for the current session, created when the target is known.
    /// </summary>
    /// <remarks>
    /// Created at attach rather than construction because the choice depends on the target: a
    /// target of this process's bitness is debugged in-process, a 32-bit one through a matching
    /// worker, since ICorDebug cannot attach across architectures.
    /// </remarks>
    private IDebugEngine Engine => _engine
        ?? throw new InvalidOperationException("No debug session is active.");

    public async Task<string> StartTestSessionAsync(
        string csprojPath,
        string? filter,
        IEnumerable<(string file, int line)>? initialBreakpoints = null,
        CancellationToken cancellationToken = default)
    {
        // Launching a test host under ICorDebug means driving vstest's own process tree, which the
        // engine has no notion of. Attaching to an already-running host is the supported route.
        await Task.CompletedTask;
        return "Error: Debugging .NET Framework test projects is not supported yet. " +
               "Run the tests, then use DebugAttach with the test host's PID.";
    }

    public async Task<string> AttachToProcessAsync(
        int pid,
        IEnumerable<(string file, int line)>? initialBreakpoints = null,
        CancellationToken cancellationToken = default)
    {
        if (_state != DebuggerService.DebugState.NotStarted)
            return "Error: A debug session is already active. Call DebugStop first.";

        var specs = BuildSpecs(initialBreakpoints);

        try
        {
            _engine = DebugEngineFactory.ForProcess(pid);
            StartPump();
            _state = DebuggerService.DebugState.Starting;
            Engine.Attach(pid, specs, EngineRuntime.NetFramework);
        }
        catch (Exception ex)
        {
            _state = DebuggerService.DebugState.NotStarted;
            _engine = null;
            return $"Error: Could not attach to process {pid}: {ex.Message}";
        }

        _state = DebuggerService.DebugState.Running;

        var sb = new StringBuilder();
        sb.AppendLine($"Attached to process {pid} using the ICorDebug engine (.NET Framework).");
        if (_breakpoints.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Breakpoints:**");
            foreach (var bp in _breakpoints.Values.OrderBy(b => b.Id))
                sb.AppendLine($"  #{bp.Id} — {Path.GetFileName(bp.FilePath)}:{bp.Line}");
        }

        return sb.ToString();
    }

    public Task<(string Message, int? BreakpointId)> SetBreakpointAsync(
        string filePath, int line, string? condition = null, CancellationToken cancellationToken = default)
    {
        if (_state == DebuggerService.DebugState.NotStarted)
            return Task.FromResult<(string, int?)>(("Error: No debug session is active.", null));

        var id = Interlocked.Increment(ref _nextBreakpointId);
        var spec = new BreakpointSpec
        {
            Id = id.ToString(),
            FilePath = PathHelper.NormalizePath(filePath),
            Line = (uint)line,
            Condition = condition ?? "",
            Enabled = true,
        };

        try
        {
            Engine.AddBreakpoint(spec);
        }
        catch (Exception ex)
        {
            return Task.FromResult<(string, int?)>(($"Error: {ex.Message}", null));
        }

        _breakpoints[id] = new DebuggerService.BreakpointInfo(id, spec.FilePath, line);

        // A breakpoint that cannot bind yet stays pending until a matching module loads, which is
        // how code in shadow-copied and generated ASP.NET assemblies gets caught.
        return Task.FromResult<(string, int?)>(
            ($"Breakpoint #{id} set at {Path.GetFileName(spec.FilePath)}:{line}.", id));
    }

    public Task<string> RemoveBreakpointAsync(int breakpointId, CancellationToken cancellationToken = default)
    {
        if (!_breakpoints.TryRemove(breakpointId, out var info))
            return Task.FromResult($"Error: No breakpoint #{breakpointId}.");

        Engine.RemoveBreakpoint(info.FilePath, info.Line);
        return Task.FromResult($"Breakpoint #{breakpointId} removed.");
    }

    public Task<string> ContinueAsync(CancellationToken cancellationToken = default) =>
        ResumeAsync(() => Engine.Continue(), cancellationToken);

    public Task<string> StepInAsync(CancellationToken cancellationToken = default) =>
        ResumeAsync(() => Engine.Step(StepKind.Into), cancellationToken);

    public Task<string> StepOverAsync(CancellationToken cancellationToken = default) =>
        ResumeAsync(() => Engine.Step(StepKind.Over), cancellationToken);

    public Task<string> StepOutAsync(CancellationToken cancellationToken = default) =>
        ResumeAsync(() => Engine.Step(StepKind.Out), cancellationToken);

    /// <summary>
    /// Issues a resuming command and waits for the next stop, so the caller gets the new position
    /// rather than returning while the target is still running.
    /// </summary>
    private async Task<string> ResumeAsync(Action resume, CancellationToken cancellationToken)
    {
        if (_state == DebuggerService.DebugState.NotStarted)
            return "Error: No debug session is active.";
        if (_exited)
            return "The process has exited.";

        lock (_gate) _currentFrame = null;

        // Drain any stop signalled before this command so the wait below cannot return instantly.
        while (_stopped.CurrentCount > 0)
            await _stopped.WaitAsync(0, cancellationToken);

        try
        {
            resume();
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }

        _state = DebuggerService.DebugState.Running;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(StopTimeout);

        try
        {
            await _stopped.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "The target is still running (no breakpoint hit within the timeout). " +
                   "Use DebugStatus to check, or set a breakpoint that will be reached.";
        }

        if (_exited)
            return "The process exited.";

        var frame = CurrentFrame;
        return frame is null
            ? "Stopped."
            : FormatPosition(frame);
    }

    public Task<string> EvaluateAsync(string expression, CancellationToken cancellationToken = default) =>
        RequireStopped(async () =>
        {
            var (ok, value, error) = await Engine.EvaluateAsync(0, expression);
            return ok
                ? $"`{expression}` = {value}"
                // The engine resolves argument/local paths and fields, but not computed
                // properties or method calls.
                : $"Error: {(string.IsNullOrEmpty(error) ? "could not evaluate expression" : error)}";
        });

    public Task<string> GetLocalsAsync(CancellationToken cancellationToken = default) =>
        RequireStopped(async () =>
        {
            var variables = await Engine.VariablesAsync(0);
            if (variables.Count == 0)
                return "No locals in scope.";

            var sb = new StringBuilder();
            sb.AppendLine("**Locals:**");
            foreach (var variable in variables)
                sb.AppendLine($"  {variable.Name} = {variable.Value}");
            return sb.ToString();
        });

    public Task<string> GetStackTraceAsync(CancellationToken cancellationToken = default) =>
        RequireStopped(async () =>
        {
            var frames = await Engine.StackTraceAsync();
            if (frames.Count == 0)
                return "No stack frames available.";

            var sb = new StringBuilder();
            sb.AppendLine("**Stack trace:**");
            foreach (var frame in frames)
            {
                var location = string.IsNullOrEmpty(frame.FilePath)
                    ? ""
                    : $" ({Path.GetFileName(frame.FilePath)}:{frame.Line})";
                sb.AppendLine($"  #{frame.Index} {frame.Method}{location}");
            }
            return sb.ToString();
        });

    private async Task<string> RequireStopped(Func<Task<string>> action)
    {
        if (_state == DebuggerService.DebugState.NotStarted)
            return "Error: No debug session is active.";
        if (CurrentFrame is null)
            return "Error: The target is running. It must be stopped at a breakpoint first.";

        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    public string GetStatus()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**State:** {_state}");
        sb.AppendLine("**Engine:** ICorDebug (.NET Framework)");

        if (!_breakpoints.IsEmpty)
        {
            sb.AppendLine();
            sb.AppendLine("**Breakpoints:**");
            foreach (var bp in _breakpoints.Values.OrderBy(b => b.Id))
                sb.AppendLine($"  #{bp.Id} — {Path.GetFileName(bp.FilePath)}:{bp.Line}");
        }

        if (CurrentFrame is { } frame)
        {
            sb.AppendLine();
            sb.Append(FormatPosition(frame));
        }

        if (!_output.IsEmpty)
        {
            sb.AppendLine();
            sb.AppendLine("**Recent output:**");
            foreach (var line in _output.TakeLast(20))
                sb.AppendLine($"  {line}");
        }

        return sb.ToString();
    }

    public string Stop()
    {
        try
        {
            _engine?.Terminate();
        }
        catch
        {
            // The target may already be gone; the session is being torn down either way.
        }

        _engine = null;
        _breakpoints.Clear();
        lock (_gate) _currentFrame = null;
        _state = DebuggerService.DebugState.NotStarted;
        return "Debug session stopped.";
    }

    private List<BreakpointSpec> BuildSpecs(IEnumerable<(string file, int line)>? initialBreakpoints)
    {
        var specs = new List<BreakpointSpec>();
        foreach (var (file, line) in initialBreakpoints ?? [])
        {
            var id = Interlocked.Increment(ref _nextBreakpointId);
            var normalized = PathHelper.NormalizePath(file);
            specs.Add(new BreakpointSpec
            {
                Id = id.ToString(),
                FilePath = normalized,
                Line = (uint)line,
                Enabled = true,
            });
            _breakpoints[id] = new DebuggerService.BreakpointInfo(id, normalized, line);
        }

        return specs;
    }

    /// <summary>
    /// Consumes the engine's event stream, translating stops into the state the tools poll and
    /// releasing anyone waiting inside <see cref="ResumeAsync"/>.
    /// </summary>
    private void StartPump()
    {
        _pump ??= Task.Run(async () =>
        {
            await foreach (var e in Engine.Events.ReadAllAsync())
            {
                switch (e.Kind)
                {
                    case DebugEventKind.Breakpoint:
                    case DebugEventKind.Step:
                    case DebugEventKind.Paused:
                    case DebugEventKind.Exception:
                        lock (_gate)
                        {
                            _currentFrame = new DebuggerService.StoppedFrame(
                                Reason: e.Kind.ToString().ToLowerInvariant(),
                                Function: e.MethodName,
                                FilePath: e.FilePath,
                                Line: (int)e.Line,
                                BreakpointNumber: int.TryParse(e.BreakpointId, out var id) ? id : 0);
                        }
                        _state = DebuggerService.DebugState.Stopped;
                        _stopped.Release();
                        break;

                    case DebugEventKind.Exited:
                        _exited = true;
                        _state = DebuggerService.DebugState.Exited;
                        lock (_gate) _currentFrame = null;
                        _stopped.Release();
                        break;

                    case DebugEventKind.Output:
                        _output.Enqueue(e.Message);
                        while (_output.Count > 200)
                            _output.TryDequeue(out _);
                        break;
                }
            }
        });
    }

    private static string FormatPosition(DebuggerService.StoppedFrame frame)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**Stopped:** {frame.Reason}");
        if (!string.IsNullOrEmpty(frame.Function))
            sb.AppendLine($"**Function:** {frame.Function}");
        if (!string.IsNullOrEmpty(frame.FilePath))
            sb.AppendLine($"**Location:** {Path.GetFileName(frame.FilePath)}:{frame.Line}");
        return sb.ToString();
    }

    public void Dispose()
    {
        try { _engine?.Dispose(); } catch { }
        _engine = null;
        try { _stopped.Dispose(); } catch { }
    }
}

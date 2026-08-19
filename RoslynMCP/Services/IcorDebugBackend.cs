using System.Collections.Concurrent;
using System.Text;
using RoslynMCP.Debugger;
using RoslynMCP.Services.Debugging;
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
internal sealed class IcorDebugBackend : IDebugBackend, IDebugNoticeSource
{
    /// <summary>How long to wait for the target to stop again after a resume.</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);

    private IDebugEngine? _engine;
    private readonly ConcurrentDictionary<int, DebuggerService.BreakpointInfo> _breakpoints = new();
    private readonly ConcurrentQueue<string> _output = new();

    /// <summary>Maps the numbers DAP calls variable references onto the engine's value paths.</summary>
    private readonly VariableHandles _handles = new();
    private readonly SemaphoreSlim _stopped = new(0);
    private readonly Lock _gate = new();

    private DebuggerService.StoppedFrame? _currentFrame;
    private DebuggerService.DebugState _state = DebuggerService.DebugState.NotStarted;
    private Task? _pump;
    private int _nextBreakpointId = 1;

    /// <summary>The frame Evaluate and locals read from; the user walks the stack with
    /// DebugSelectFrame.</summary>
    private int _selectedFrame;
    private bool _exited;

    public DebuggerService.StoppedFrame? CurrentFrame
    {
        get { lock (_gate) return _currentFrame; }
    }

    /// <inheritdoc />
    public int? DebuggeePid => _debuggeePid == 0 ? null : _debuggeePid;

    private int _debuggeePid;

    /// <inheritdoc />
    public event Action<DebugNotice>? Notice;

    /// <inheritdoc />
    public long StopSequence => Interlocked.Read(ref _stopSequence);

    private long _stopSequence;

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
        // vstest owns the process tree, so the host cannot be launched under the debugger
        // directly. VSTEST_HOST_DEBUG makes it suspend and print its pid instead, which is the
        // window to attach in — the same trick the CoreCLR backend uses.
        var (pid, error) = await Testing.TestRunService.StartForDebugAsync(
            csprojPath, filter, cancellationToken);

        if (pid == 0)
            return $"Error: {error ?? "the test host did not start."}";

        string attached = await AttachToProcessAsync(pid, initialBreakpoints, cancellationToken);
        return attached.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
            ? attached
            : $"{attached}\nTest host pid {pid}. Use DebugContinue to start the tests.";
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
            Engine.SetDisplayOptions(Config.DebuggerViewOptions.Current);
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

    /// <summary>
    /// Starts a program under the debugger, suspended, so a breakpoint in <c>Main</c> or a static
    /// constructor is hit — which attaching can never manage, since by the time it lands that code
    /// has already run.
    /// </summary>
    public async Task<string> LaunchAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        string? workingDirectory,
        IEnumerable<(string file, int line)>? initialBreakpoints = null,
        CancellationToken cancellationToken = default)
    {
        if (_state != DebuggerService.DebugState.NotStarted)
            return "Error: A debug session is already active. Call DebugStop first.";
        if (!File.Exists(executable))
            return $"Error: '{executable}' does not exist. Build the project first.";

        var specs = BuildSpecs(initialBreakpoints);

        try
        {
            // The engine has to match the *target's* bitness, and a Framework build can be x86
            // while this host is x64; the factory picks the worker from the executable itself.
            _engine = DebugEngineFactory.ForExecutable(executable);
            Engine.SetDisplayOptions(Config.DebuggerViewOptions.Current);
            StartPump();
            _state = DebuggerService.DebugState.Starting;
            Engine.Launch(
                executable, arguments, specs, environment,
                workingDirectory ?? Path.GetDirectoryName(executable),
                EngineRuntime.NetFramework);
        }
        catch (Exception ex)
        {
            _state = DebuggerService.DebugState.NotStarted;
            _engine = null;
            return $"Error: Could not launch '{Path.GetFileName(executable)}': {ex.Message}";
        }

        _state = DebuggerService.DebugState.Running;
        await Task.CompletedTask;

        var sb = new StringBuilder();
        sb.AppendLine($"Launched {Path.GetFileName(executable)} under the ICorDebug engine (.NET Framework).");
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
        string filePath, int line, string? condition = null, string? hitCondition = null,
        string? logMessage = null, CancellationToken cancellationToken = default)
    {
        // Emulated a layer up, in PublishingDebugBackend.
        _ = (hitCondition, logMessage);

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

        // Every path a reference points at describes a value in the frame that is about to be
        // left, so the numbers must not survive the resume that invalidates them.
        _handles.Reset();

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
            var (ok, value, error) = await Engine.EvaluateAsync((uint)_selectedFrame, expression);
            return ok
                ? $"`{expression}` = {value}"
                // The engine resolves argument/local paths and fields, but not computed
                // properties or method calls.
                : $"Error: {(string.IsNullOrEmpty(error) ? "could not evaluate expression" : error)}";
        });

    public Task<string> GetLocalsAsync(CancellationToken cancellationToken = default) =>
        RequireStopped(async () =>
        {
            var variables = await Engine.VariablesAsync((uint)_selectedFrame);
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

    /// <summary>
    /// Retargets a live session at a new view policy, so turning a display string off in
    /// configuration takes effect at the next expansion rather than at the next session.
    /// </summary>
    public void ApplyViewOptions(RoslynMCP.Debugger.DebugDisplayOptions options)
    {
        if (_engine is null)
            return;

        _engine.SetDisplayOptions(options);

        // The numbers already handed out describe values filtered under the old policy; a proxy
        // that just went away leaves paths that no longer resolve.
        _handles.Reset();
    }

    // --- Structured views ---

    public async Task<IReadOnlyList<StackFrameInfo>> GetStackFramesAsync(
        CancellationToken cancellationToken = default)
    {
        if (CurrentFrame is null)
            return [];

        try
        {
            var frames = await Engine.StackTraceAsync();
            return frames
                .Select(f => new StackFrameInfo(
                    (int)f.Index,
                    string.IsNullOrEmpty(f.Method) ? "unknown" : f.Method,
                    f.FilePath,
                    (int)f.Line,
                    (int)f.Column,
                    IsExternal: string.IsNullOrEmpty(f.FilePath)))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public async Task<IReadOnlyList<VariableInfo>> GetVariablesAsync(
        int frameId, CancellationToken cancellationToken = default)
    {
        if (CurrentFrame is null)
            return [];

        try
        {
            var frame = (uint)Math.Max(0, frameId);
            return Describe(await Engine.VariablesAsync(frame), frameId);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Expands one value into its children — fields, elements, or the members of the debugger view
    /// its type declares through <c>DebuggerTypeProxy</c>.
    /// </summary>
    /// <remarks>
    /// The engine addresses children by path, not by handle, so a reference is nothing more than
    /// this session's number for one <c>frame|path</c> pair. Paths are stable across stops in a
    /// way handles are not, which is why the reference is minted here rather than in the engine.
    /// </remarks>
    public async Task<IReadOnlyList<VariableInfo>> GetVariableChildrenAsync(
        int variablesReference, CancellationToken cancellationToken = default)
    {
        if (CurrentFrame is null)
            return [];
        if (_handles.Expression(variablesReference) is not { } handle)
            return [];

        var (frameId, path) = DecodeHandle(handle);

        try
        {
            return Describe(await Engine.ExpandAsync((uint)Math.Max(0, frameId), path), frameId);
        }
        catch
        {
            return [];
        }
    }

    private List<VariableInfo> Describe(IEnumerable<RoslynMCP.Debugger.DebugVariable> variables, int frameId) =>
        variables
            .Select(v => new VariableInfo(
                v.Name,
                v.Value,
                v.Type,
                VariablesReference: v.VariablesReference.Length == 0
                    ? 0
                    : _handles.For($"{frameId}|{v.VariablesReference}"),
                // The engine reports children only when asked for them, so a count would cost a
                // second round trip to learn what the client discovers by expanding anyway.
                NamedChildCount: 0,
                IndexedChildCount: 0,
                Evaluable: v.Settable))
            .ToList();

    private static (int FrameId, string Path) DecodeHandle(string handle)
    {
        var separator = handle.IndexOf('|');
        return separator < 0
            ? (0, handle)
            : (int.TryParse(handle[..separator], out var frame) ? frame : 0, handle[(separator + 1)..]);
    }

    public async Task<string> SelectFrameAsync(int frameId, CancellationToken cancellationToken = default)
    {
        if (CurrentFrame is null)
            return "Error: the target is running. It must be stopped first.";
        if (frameId < 0)
            return "Error: frame numbers start at 0 (the innermost frame).";

        var frames = await GetStackFramesAsync(cancellationToken);
        if (frames.Count > 0 && frameId >= frames.Count)
            return $"Error: the stack has {frames.Count} frames; #{frameId} does not exist.";

        _selectedFrame = frameId;
        var frame = frames.FirstOrDefault(f => f.Id == frameId);

        return frame is null
            ? $"Selected frame #{frameId}."
            : $"Selected frame #{frameId}: {frame.Name}" +
              (frame.FilePath.Length == 0 ? "" : $" at {Path.GetFileName(frame.FilePath)}:{frame.Line}");
    }

    public async Task<(bool Ok, string Value, string Error)> SetVariableAsync(
        string name, string value, int frameId = 0, CancellationToken cancellationToken = default)
    {
        if (CurrentFrame is null)
            return (false, "", "The target is running. It must be stopped first.");

        try
        {
            var (ok, variable, error) = await Engine.SetVariableAsync((uint)Math.Max(0, frameId), name, value);
            return ok ? (true, variable?.Value ?? value, "") : (false, "", error);
        }
        catch (Exception ex)
        {
            return (false, "", ex.Message);
        }
    }

    /// <summary>
    /// One thread, the stopped one. The engine surface exposes no thread list, and inventing ids
    /// for threads the tools cannot then select would be worse than reporting only what works.
    /// </summary>
    public Task<IReadOnlyList<ThreadInfo>> GetThreadsAsync(CancellationToken cancellationToken = default)
    {
        if (_state is DebuggerService.DebugState.NotStarted or DebuggerService.DebugState.Exited)
            return Task.FromResult<IReadOnlyList<ThreadInfo>>([]);

        string state = CurrentFrame is null ? "running" : "stopped";
        return Task.FromResult<IReadOnlyList<ThreadInfo>>([new ThreadInfo(1, "Main Thread", state)]);
    }

    public Task<ExceptionDetail?> GetExceptionInfoAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentFrame?.ExceptionName is not { Length: > 0 } typeName)
            return Task.FromResult<ExceptionDetail?>(null);

        return Task.FromResult<ExceptionDetail?>(new ExceptionDetail(
            typeName, CurrentFrame.ExceptionMessage ?? "", StackTrace: null, BreakMode: "always"));
    }

    /// <summary>
    /// Applies the exception filters. Unhandled exceptions always stop; the choice is whether
    /// first-chance ones do too, which is what the <c>all</c> filter means here.
    /// </summary>
    public Task<string> SetExceptionFiltersAsync(
        ExceptionFilters filters, CancellationToken cancellationToken = default)
    {
        if (_engine is null)
            return Task.FromResult("Error: No debug session is active.");

        _engine.SetExceptionPolicy(filters.All);

        return Task.FromResult(filters.All
            ? "Breaking on every thrown exception, handled or not."
            : "Breaking on unhandled exceptions only.");
    }

    public async Task<string> RunToLocationAsync(
        string filePath, int line, CancellationToken cancellationToken = default)
    {
        if (_state == DebuggerService.DebugState.NotStarted)
            return "Error: No debug session is active.";

        var response = await Engine.RunToLocationAsync(new RunToLocationRequest
        {
            Location = new SourceRange { FilePath = filePath, Line = (uint)Math.Max(0, line) },
        });

        if (!response.Ok)
            return $"Error: {response.Error}";

        // The engine sets a one-shot breakpoint and resumes; the stop arrives on the event pump
        // like any other, so the wait is the same one Continue uses.
        return await ResumeAsync(() => { }, cancellationToken);
    }

    public async Task<string> SetNextStatementAsync(
        string filePath, int line, CancellationToken cancellationToken = default)
    {
        if (CurrentFrame is null)
            return "Error: The instruction pointer can only be moved while the target is stopped.";

        var response = await Engine.SetNextStatementAsync(new SetNextStatementRequest
        {
            FrameIndex = (uint)_selectedFrame,
            Location = new SourceRange { FilePath = filePath, Line = (uint)Math.Max(0, line) },
        });

        if (!response.Ok)
            return $"Error: {response.Error}";

        int actual = (int)(response.Actual?.Line ?? (uint)line);
        return $"The next statement is now {Path.GetFileName(filePath)}:{actual}.";
    }

    public async Task<IReadOnlyList<ModuleInfo>> GetModulesAsync(
        CancellationToken cancellationToken = default)
    {
        if (_state == DebuggerService.DebugState.NotStarted)
            return [];

        return [.. (await Engine.ModulesAsync()).Select(m =>
            new ModuleInfo(m.Name, m.Path, m.SymbolsLoaded, m.SymbolPath, m.Runtime))];
    }

    public async Task<string> DetachAsync(CancellationToken cancellationToken = default)
    {
        if (_engine is null)
            return "Error: No debug session is active.";

        var (ok, error) = await _engine.DetachAsync();
        if (!ok)
            return $"Error: {error}";

        _state = DebuggerService.DebugState.NotStarted;
        return "Detached. The process is still running.";
    }

    /// <summary>
    /// Break All. Waits for the stop rather than returning as soon as the request is sent, so the
    /// caller gets a position instead of a promise.
    /// </summary>
    public Task<string> InterruptAsync(CancellationToken cancellationToken = default) =>
        ResumeAsync(() => Engine.Pause(), cancellationToken);

    /// <summary>
    /// Applies one hot reload delta to a module loaded in the debuggee.
    /// </summary>
    /// <remarks>
    /// This is the only route onto the desktop runtime: .NET Framework has no in-process metadata
    /// updater, so the edit has to go through the debugger that is already attached. The engine
    /// enabled EnC JIT flags on every module as it loaded, which is what makes the apply possible
    /// at all — a module JITted without them refuses the change.
    /// </remarks>
    public async Task<(bool Ok, string Error)> ApplyDeltaAsync(
        string assemblyName, byte[] metadata, byte[] il, byte[] pdb,
        CancellationToken cancellationToken = default)
    {
        if (_engine is null)
            return (false, "No .NET Framework debug session is attached.");

        // An edit needs the target stopped, so a running one is broken into first and resumed
        // afterwards — the user asked to apply an edit, not to be told to go and press pause.
        // It has to be a full Break All rather than a bare suspend: applying immediately after
        // ICorDebugProcess::Stop faults inside ApplyChanges instead of failing.
        bool paused = false;
        if (CurrentFrame is null)
        {
            string result = await InterruptAsync(cancellationToken);
            if (CurrentFrame is null)
                return (false, $"the target could not be suspended to apply the edit: {result}");
            paused = true;
        }

        try
        {
            return await _engine.ApplyDeltaAsync(assemblyName, metadata, il, pdb);
        }
        finally
        {
            // Back to where it was. A hot reload that silently leaves the app suspended looks
            // exactly like a hot reload that hung it.
            if (paused)
                _ = ContinueAsync(CancellationToken.None);
        }
    }

    /// <summary>Reads the type out of a <c>Type: message</c> event line.</summary>
    private static string ExceptionTypeOf(string message)
    {
        int colon = message.IndexOf(':');
        if (colon <= 0)
            return message;

        string candidate = message[..colon].Trim();
        // A type name, not a sentence that happens to contain a colon.
        return candidate.Contains(' ') ? message : candidate;
    }

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

    /// <summary>
    /// Asks the debuggee to shut down the way the outside world would ask it to, and only kills
    /// it if it will not go.
    /// </summary>
    /// <remarks>
    /// Stopping a debug session used to be indistinguishable from the process crashing, which for
    /// anything hosting services meant connections left open, buffers unflushed and
    /// <c>StopAsync</c> never called. The session state is reset through <see cref="Stop"/>
    /// either way, since the engine is finished by the time this returns.
    /// </remarks>
    public async Task<(bool Graceful, string Message)> ShutdownAsync(
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var engine = _engine;
        if (engine is null)
            return (false, Stop());

        var (graceful, error) = await engine.ShutdownAsync(timeout);
        Stop();

        return graceful
            ? (true, "Debug session stopped; the debuggee shut down cleanly.")
            : (false, $"Debug session stopped ({error}).");
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
        _handles.Reset();
        lock (_gate) _currentFrame = null;
        _state = DebuggerService.DebugState.NotStarted;

        // Everything a second session on this instance would inherit. The pump is bound to the
        // engine that just went away, so leaving it set means the next session never starts one
        // and never sees a stop; leaving _exited set makes every resume answer "already exited".
        _pump = null;
        _exited = false;
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
        // A pump left over from a session that failed or ended has completed, and `??=` would
        // keep it — silently leaving the new session with no event stream at all.
        if (_pump is { IsCompleted: true })
            _pump = null;

        _pump ??= Task.Run(async () =>
        {
            await foreach (var e in Engine.Events.ReadAllAsync())
            {
                // Every event carries it, and the first one to do so is the launch: recorded here
                // so a worker-hosted session reports the same PID as an in-process one.
                if (e.ProcessId != 0)
                    Interlocked.Exchange(ref _debuggeePid, e.ProcessId);

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
                                BreakpointNumber: int.TryParse(e.BreakpointId, out var id) ? id : 0,
                                // The engine reports the exception as the event message; a
                                // "Type: message" prefix is the only type information available.
                                ExceptionName: e.Kind == DebugEventKind.Exception
                                    ? ExceptionTypeOf(e.Message)
                                    : null,
                                ExceptionMessage: e.Kind == DebugEventKind.Exception ? e.Message : null,
                                ExceptionStage: e.Kind == DebugEventKind.Exception ? "throw" : null);
                        }
                        _selectedFrame = 0;
                        _state = DebuggerService.DebugState.Stopped;
                        Interlocked.Increment(ref _stopSequence);
                        // Raised before the release so a listener sees the stop even when nothing
                        // was waiting for one — which is every breakpoint hit in a running app.
                        Raise(DebugNoticeKind.Stopped, e, StopReason(e.Kind));
                        _stopped.Release();
                        break;

                    case DebugEventKind.Exited:
                        _exited = true;
                        _state = DebuggerService.DebugState.Exited;
                        lock (_gate) _currentFrame = null;
                        Raise(DebugNoticeKind.Exited, e);
                        _stopped.Release();
                        break;

                    case DebugEventKind.Output:
                        Remember(e.Message);
                        Raise(DebugNoticeKind.Output, e);
                        break;

                    case DebugEventKind.Diagnostic:
                        Remember(e.Message);
                        Raise(DebugNoticeKind.Diagnostic, e);
                        break;

                    case DebugEventKind.Module:
                        Raise(DebugNoticeKind.Module, e);
                        break;

                    case DebugEventKind.BreakpointBound:
                        Raise(DebugNoticeKind.BreakpointBound, e);
                        break;

                    case DebugEventKind.BreakpointUnbound:
                        Raise(DebugNoticeKind.BreakpointUnbound, e);
                        break;
                }
            }
        });
    }

    private void Remember(string line)
    {
        _output.Enqueue(line);
        while (_output.Count > 200)
            _output.TryDequeue(out _);
    }

    /// <summary>
    /// Republishes an engine event as a notice. A handler that throws must not kill the pump —
    /// the stops that follow it are what the whole session depends on.
    /// </summary>
    private void Raise(DebugNoticeKind kind, DebugEvent e, string? message = null)
    {
        if (Notice is not { } handler)
            return;

        try
        {
            handler(new DebugNotice(
                kind,
                message ?? e.Message,
                e.FilePath,
                (int)e.Line,
                int.TryParse(e.BreakpointId, out var id) ? id : 0));
        }
        catch
        {
            // A client that cannot take a diagnostic still gets its breakpoints.
        }
    }

    /// <summary>The engine's stop kind as DAP names it.</summary>
    private static string StopReason(DebugEventKind kind) => kind switch
    {
        DebugEventKind.Step => "step",
        DebugEventKind.Paused => "pause",
        DebugEventKind.Exception => "exception",
        _ => "breakpoint",
    };

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

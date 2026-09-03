using System.Collections.Concurrent;
using System.Text;
using RoslynMCP.Debugger;
using RoslynMCP.Services.Debugging;
using DebuggerEngine = RoslynMCP.Debugger.DebugSession;
using EngineRuntime = RoslynMCP.Debugger.DebugRuntime;

namespace RoslynMCP.Services;

/// <summary>
/// Backs the <c>Debug*</c> tools with the ICorDebug engine, which is the only way to debug
/// .NET Framework targets (netcoredbg speaks to CoreCLR only) and an opt-in for CoreCLR ones.
/// </summary>
/// <remarks>
/// Adapts the engine's event-stream shape to the request/response shape the tools expect: the
/// engine reports stops asynchronously, so this waits for the next stop after each resuming
/// command rather than returning while the target is still running.
/// </remarks>
internal sealed class IcorDebugBackend : IDebugBackend, IDebugNoticeSource
{
    /// <summary>
    /// Which runtime the target carries, which decides how the engine gets into it: .NET Framework
    /// through the CLR meta-host, CoreCLR through dbgshim.
    /// </summary>
    /// <remarks>
    /// Defaulted to .NET Framework because that is the target this backend has always been given,
    /// and because it is the only runtime for which the choice is forced.
    /// </remarks>
    private readonly EngineRuntime _runtime;

    public IcorDebugBackend(EngineRuntime runtime = EngineRuntime.NetFramework) =>
        _runtime = runtime == EngineRuntime.Unspecified ? EngineRuntime.NetFramework : runtime;

    /// <summary>The runtime's name as it appears in messages to the user.</summary>
    private string RuntimeName =>
        _runtime == EngineRuntime.CoreClr ? ".NET" : ".NET Framework";

    /// <summary>
    /// Whether hot reload deltas go through this session rather than through the in-process
    /// updater.
    /// </summary>
    /// <remarks>
    /// Only .NET Framework, which has no updater of its own. Asked before a delta is routed here,
    /// so a session that would refuse it is not mistaken for the one that will take it.
    /// </remarks>
    public bool AppliesDeltas => _runtime == EngineRuntime.NetFramework;

    /// <summary>
    /// The refusal a session that does not apply deltas answers with.
    /// </summary>
    /// <remarks>
    /// A constant because two things have to agree on it: this backend produces it, and the hot
    /// reload fan-out matches it to tell a skip from a failure. Worded apart, a working reload
    /// would start reporting errors for every module while still applying every edit.
    /// </remarks>
    public const string NotADeltaTarget =
        "this session does not debug .NET Framework, so it cannot apply a delta";


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
            PushViewOptions(Config.DebuggerViewOptions.Current);
            StartPump();
            _state = DebuggerService.DebugState.Starting;
            Engine.Attach(pid, specs, _runtime);
        }
        catch (Exception ex)
        {
            _state = DebuggerService.DebugState.NotStarted;
            _engine = null;
            return $"Error: Could not attach to process {pid}: {ex.Message}";
        }

        _state = DebuggerService.DebugState.Running;

        var sb = new StringBuilder();
        sb.AppendLine($"Attached to process {pid} using the ICorDebug engine ({RuntimeName}).");
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
        // A bare command name is left to the OS to find on PATH. A project built with
        // UseAppHost=false has no executable of its own and is launched as `dotnet app.dll`, and
        // reporting that as "dotnet does not exist, build the project first" diagnoses the wrong
        // thing about a project that built perfectly well.
        bool named = Path.GetFileName(executable) != executable;
        if (named && !File.Exists(executable))
            return $"Error: '{executable}' does not exist. Build the project first.";

        var specs = BuildSpecs(initialBreakpoints);

        try
        {
            // The engine has to match the *target's* bitness, and a Framework build can be x86
            // while this host is x64; the factory picks the worker from the executable itself.
            _engine = DebugEngineFactory.ForExecutable(executable);
            PushViewOptions(Config.DebuggerViewOptions.Current);
            StartPump();
            _state = DebuggerService.DebugState.Starting;
            Engine.Launch(
                executable, arguments, specs, environment,
                workingDirectory ?? Path.GetDirectoryName(executable),
                _runtime);
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
        sb.AppendLine($"Launched {Path.GetFileName(executable)} under the ICorDebug engine ({RuntimeName}).");
        if (_breakpoints.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Breakpoints:**");
            foreach (var bp in _breakpoints.Values.OrderBy(b => b.Id))
                sb.AppendLine($"  #{bp.Id} — {Path.GetFileName(bp.FilePath)}:{bp.Line}");
        }
        return sb.ToString();
    }

    /// <inheritdoc />
    /// <remarks>The engine applies both on the runtime's callback thread, so a hit that the rule
    /// excludes never becomes a stop and never leaves the debuggee's process.</remarks>
    public bool AppliesBreakpointRulesInEngine => true;

    /// <inheritdoc />
    /// <remarks>The type lists are matched against the thrown value inside the callback, before
    /// the stop is raised, so an excluded exception costs a comparison rather than a suspend.</remarks>
    public bool AppliesExceptionTypeFiltersInEngine => true;

    public async Task<(string Message, int? BreakpointId)> SetBreakpointAsync(
        string filePath, int line, string? condition = null, string? hitCondition = null,
        string? logMessage = null, CancellationToken cancellationToken = default)
    {
        if (_state == DebuggerService.DebugState.NotStarted)
            return ("Error: No debug session is active.", null);

        var id = Interlocked.Increment(ref _nextBreakpointId);
        var spec = new BreakpointSpec
        {
            Id = id.ToString(),
            FilePath = PathHelper.NormalizePath(filePath),
            Line = (uint)line,
            Condition = condition ?? "",
            HitCondition = hitCondition ?? "",
            LogMessage = logMessage ?? "",
            Enabled = true,
        };

        // A line in a decompiled or fetched file names no PDB document, so document binding
        // cannot find it — but the file's own sequence-point map can be read backwards to the
        // MethodDef token and IL offset the line compiles from, and the engine binds on that.
        string note = "";
        var target = await ExternalSource.DebugSourceMapper.TryMapAsync(
            spec.FilePath, line, cancellationToken);
        if (target is not null)
        {
            spec.ModulePath = target.AssemblyPath;
            spec.MethodToken = target.MethodToken;
            spec.IlOffset = target.IlOffset;
            spec.Line = (uint)target.Line;
            spec.Column = (uint)target.Column;
            note = target.Exact
                ? $" ({target.Origin}, IL offset 0x{target.IlOffset:X})"
                : $" ({target.Origin}: no line-level offsets exist, so it binds at the entry of" +
                  $" {target.MethodDisplayName})";
        }

        try
        {
            Engine.AddBreakpoint(spec);
        }
        catch (Exception ex)
        {
            return ($"Error: {ex.Message}", null);
        }

        _breakpoints[id] = new DebuggerService.BreakpointInfo(id, spec.FilePath, (int)spec.Line);

        // A breakpoint that cannot bind yet stays pending until a matching module loads, which is
        // how code in shadow-copied and generated ASP.NET assemblies gets caught.
        return ($"Breakpoint #{id} set at {Path.GetFileName(spec.FilePath)}:{spec.Line}{note}.", id);
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
        if (frame is null)
            return "Stopped.";

        return FormatPosition(await EnrichStoppedFrameAsync(frame, cancellationToken));
    }

    /// <summary>
    /// A stop with no source — external code — resolved through the enriched top stack frame,
    /// so a step into a dependency reports the file the executing statement was resolved to.
    /// The stored current frame is updated too, so the status surface agrees.
    /// </summary>
    private async Task<DebuggerService.StoppedFrame> EnrichStoppedFrameAsync(
        DebuggerService.StoppedFrame frame, CancellationToken cancellationToken)
    {
        if (frame.FilePath.Length > 0)
            return frame;

        try
        {
            var frames = await GetStackFramesAsync(cancellationToken);
            if (frames.FirstOrDefault() is not { FilePath.Length: > 0 } top)
                return frame;

            var enriched = frame with
            {
                FilePath = top.FilePath,
                Line = top.Line,
                SourceOrigin = top.SourceOrigin,
            };

            lock (_gate)
            {
                if (ReferenceEquals(_currentFrame, frame))
                    _currentFrame = enriched;
            }

            return enriched;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ServiceLog.Warn(
                $"Could not resolve external source for the stop location: {ex.Message}",
                key: "debug-stop-source");
            return frame;
        }
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
            var frames = await GetStackFramesAsync(cancellationToken);
            if (frames.Count == 0)
                return "No stack frames available.";

            var sb = new StringBuilder();
            sb.AppendLine("**Stack trace:**");
            foreach (var frame in frames)
            {
                var location = string.IsNullOrEmpty(frame.FilePath)
                    ? ""
                    : $" ({Path.GetFileName(frame.FilePath)}:{frame.Line})";
                var origin = frame.SourceOrigin.Length > 0 ? $" ({frame.SourceOrigin})" : "";
                sb.AppendLine($"  #{frame.Id} {frame.Name}{location}{origin}");
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

        PushViewOptions(options);

        // The numbers already handed out describe values filtered under the old policy; a proxy
        // that just went away leaves paths that no longer resolve.
        _handles.Reset();
    }

    /// <summary>The assembly list the engine was last told about, so a re-push that would say the
    /// same thing can be skipped.</summary>
    private string[] _pushedUserAssemblies = [];

    /// <summary>Sends a view policy to the engine with the solution's assemblies attached.</summary>
    private void PushViewOptions(RoslynMCP.Debugger.DebugDisplayOptions options)
    {
        var full = WithUserAssemblies(options);
        _pushedUserAssemblies = full.UserAssemblies;
        Engine.SetDisplayOptions(full);
    }

    /// <summary>
    /// Re-sends the view policy when the solution has come to say something different about which
    /// assemblies are the user's.
    /// </summary>
    /// <remarks>
    /// Called at every stop, because attaching to a process and opening the solution afterwards is
    /// an ordinary order to do things in — and the only other things that push a policy are the
    /// session starting and a settings change, neither of which the user has any reason to perform
    /// just because a workspace finished loading. Skipped when the answer has not changed, so the
    /// usual stop costs one list comparison.
    /// </remarks>
    private void RefreshUserAssemblies()
    {
        if (_engine is null)
            return;

        try
        {
            var options = WithUserAssemblies(Config.DebuggerViewOptions.Current);
            if (SameAssemblies(options.UserAssemblies, _pushedUserAssemblies))
                return;

            _pushedUserAssemblies = options.UserAssemblies;
            _engine.SetDisplayOptions(options);
        }
        catch
        {
            // A workspace that cannot be read is the case this already falls back to: the engine
            // keeps the policy it has, which classifies by directory alone.
        }
    }

    /// <summary>Which decompiled types this session has already given the engine.</summary>
    private readonly HashSet<(string Module, long Stamp, string Type)> _pushedDecompiled = [];

    /// <summary>
    /// Gives the engine one decompiled type as its module's symbols, once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Once, because both halves cost: building the map copies every sequence point of every method
    /// in the type, and sending it to a worker-hosted engine serializes the lot and waits for the
    /// answer. The caller cannot skip it on its own — a frame the pushed symbols cannot answer (an
    /// IP before the type's first sequence point, say) keeps arriving without a file, because the
    /// decompiler still answers it from the type declaration — so without this the same type would
    /// be rebuilt and resent at every stop, which while stepping is once per keypress.
    /// </para>
    /// <para>
    /// Keyed by the module's write time as well as its path, so a rebuilt binary at the same path
    /// is a different key and is sent again — the same key the decompiler caches the map under.
    /// </para>
    /// </remarks>
    private async Task ShareDecompiledSymbolsAsync(
        string modulePath, string reflectionTypeName, CancellationToken cancellationToken)
    {
        if (_engine is not { } engine)
            return;

        long stamp;
        try { stamp = File.GetLastWriteTimeUtc(modulePath).Ticks; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { stamp = 0; }

        var key = (modulePath, stamp, reflectionTypeName);
        lock (_pushedDecompiled)
        {
            // Claimed before the work rather than after it, so two stack walks over the same stop
            // do not both build the same map.
            if (!_pushedDecompiled.Add(key))
                return;
        }

        var sent = false;
        try
        {
            if (await DecompiledSourceService.TrySymbolsForAsync(
                    modulePath, reflectionTypeName, cancellationToken) is { } map)
            {
                engine.AddDecompiledSymbols(modulePath, map);
                sent = true;
            }
        }
        finally
        {
            // A claim that produced nothing is given back — a decompilation that failed or was
            // cancelled must not mark the type as delivered for the rest of the session.
            if (!sent)
            {
                lock (_pushedDecompiled)
                    _pushedDecompiled.Remove(key);
            }
        }
    }

    private static bool SameAssemblies(string[] left, string[] right)
    {
        if (left.Length != right.Length)
            return false;

        // By set, not by order: the projects come out of the workspace in whatever order it holds
        // them, and a reordering is not a change worth re-marking every module for.
        return left.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(
                right.OrderBy(p => p, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Adds the open solution's output assemblies to a view policy, which is what turns Just My
    /// Code from a guess about paths into a fact about this solution.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read here rather than in the engine because the engine is often not in this process at all:
    /// a 32-bit target is debugged from a worker that has no workspace, no MSBuild, and no way to
    /// ask. Sending the names along with the rest of the policy is what makes a worker session
    /// filter the same way an in-process one does.
    /// </para>
    /// <para>
    /// No solution open leaves the list empty, which the engine reads as "nothing is known" rather
    /// than "nothing is the user's" — the difference between filtering by fact and hiding
    /// everything.
    /// </para>
    /// </remarks>
    private static RoslynMCP.Debugger.DebugDisplayOptions WithUserAssemblies(
        RoslynMCP.Debugger.DebugDisplayOptions options)
    {
        string[] assemblies;
        try
        {
            assemblies = WorkspaceService.TryGetSessionSolution() is { } solution
                ? [.. solution.Projects
                    .Select(p => p.OutputFilePath)
                    .Where(p => p is { Length: > 0 })
                    .Select(p => p!)]
                : [];
        }
        catch
        {
            assemblies = [];
        }

        var copy = options.Clone();
        copy.UserAssemblies = assemblies;
        return copy;
    }

    // --- Structured views ---

    public Task<IReadOnlyList<StackFrameInfo>> GetStackFramesAsync(
        CancellationToken cancellationToken = default) =>
        GetStackFramesAsync(0, cancellationToken);

    public async Task<IReadOnlyList<StackFrameInfo>> GetStackFramesAsync(
        int threadId, CancellationToken cancellationToken = default)
    {
        if (CurrentFrame is null)
            return [];

        try
        {
            var frames = await Engine.StackTraceAsync(threadId);
            var mapped = frames
                .Select(f => new StackFrameInfo(
                    (int)f.Index,
                    string.IsNullOrEmpty(f.Method) ? "unknown" : f.Method,
                    f.FilePath,
                    (int)f.Line,
                    (int)f.Column,
                    IsExternal: string.IsNullOrEmpty(f.FilePath),
                    ModulePath: f.ModulePath,
                    MethodToken: f.MethodToken,
                    IlOffset: f.IlOffset,
                    IsNonUserCode: f.IsNonUserCode,
                    EndLine: (int)f.EndLine,
                    EndColumn: (int)f.EndColumn))
                .ToList();
            return await ExternalFrameResolver.EnrichAsync(
                mapped, cancellationToken, ShareDecompiledSymbolsAsync);
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
    /// Every managed thread in the target, with the OS thread id each is addressed by.
    /// </summary>
    /// <remarks>
    /// Only readable while the target is stopped — enumerating app domains and threads means
    /// calling into a suspended process. While it runs there is nothing coherent to report, so the
    /// stopped thread is not guessed at: the list is empty and the caller sees "running".
    /// </remarks>
    public async Task<IReadOnlyList<ThreadInfo>> GetThreadsAsync(CancellationToken cancellationToken = default)
    {
        if (_state is DebuggerService.DebugState.NotStarted or DebuggerService.DebugState.Exited)
            return [];

        if (CurrentFrame is null || _engine is null)
            return [];

        try
        {
            var threads = await Engine.ThreadsAsync();
            return [.. threads.Select(t => new ThreadInfo(t.Id, ThreadLabel(t), t.Stopped ? "stopped" : "paused"))];
        }
        catch (Exception ex)
        {
            ServiceLog.Warn(
                $"Could not list the target's threads: {ex.Message}", key: "debug-threads");
            return [];
        }
    }

    /// <summary>
    /// How one thread reads in the editor's thread list.
    /// </summary>
    /// <remarks>
    /// ICorDebug has no managed thread name to ask for — <c>Thread.Name</c> is a field on a managed
    /// object, readable only by evaluating in the target, which is far too much to spend on a list.
    /// Where the thread currently is says more than a name would anyway, so that is what is shown
    /// when it is known.
    /// </remarks>
    private static string ThreadLabel(RoslynMCP.Debugger.DebugThread thread)
    {
        if (thread.Name.Length > 0)
            return thread.Name;
        return thread.Location.Length > 0 ? $"Thread {thread.Id} — {thread.Location}" : $"Thread {thread.Id}";
    }

    public Task<ExceptionDetail?> GetExceptionInfoAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentFrame?.ExceptionName is not { Length: > 0 } typeName)
            return Task.FromResult<ExceptionDetail?>(null);

        // The break mode is what the editor's exception popup titles itself with — "Exception has
        // occurred" against "Exception was unhandled" — so reporting the real one is the
        // difference between a popup that tells the user something and one that does not.
        return Task.FromResult<ExceptionDetail?>(new ExceptionDetail(
            typeName,
            CurrentFrame.ExceptionMessage ?? "",
            StackTrace: null,
            BreakMode: CurrentFrame.ExceptionStage == "unhandled" ? "unhandled" : "always"));
    }

    /// <summary>
    /// Applies the exception filters. The <c>all</c> filter means "stop the moment it is thrown";
    /// without it only exceptions no handler was found for stop.
    /// </summary>
    /// <remarks>
    /// The type lists go down with the policy rather than being applied to stops after the fact.
    /// That is the whole point of them: a framework that throws internally on a hot path turns
    /// "break on all exceptions" into an unusable session, and a filter only fixes that if the
    /// exceptions it rejects never suspend the process at all.
    /// </remarks>
    public Task<string> SetExceptionFiltersAsync(
        ExceptionFilters filters, CancellationToken cancellationToken = default)
    {
        if (_engine is null)
            return Task.FromResult("Error: No debug session is active.");

        var include = filters.IncludeTypes ?? [];
        var exclude = filters.ExcludeTypes ?? [];
        var unhandledInclude = filters.UnhandledIncludeTypes ?? [];
        var unhandledExclude = filters.UnhandledExcludeTypes ?? [];

        _engine.SetExceptionPolicy(new RoslynMCP.Debugger.ExceptionPolicy
        {
            // The filter list is authoritative, including when it turns the unhandled stop off:
            // a session attached only to watch should not suspend a process at the end of its
            // life. Each rule keeps its own types, so one cannot silence the other.
            Unhandled = new RoslynMCP.Debugger.ExceptionRule
            {
                Enabled = filters.UserUnhandled,
                IncludeTypes = [.. unhandledInclude],
                ExcludeTypes = [.. unhandledExclude],
            },
            Caught = new RoslynMCP.Debugger.ExceptionRule
            {
                Enabled = filters.All,
                IncludeTypes = [.. include],
                ExcludeTypes = [.. exclude],
            },
        });

        var scope = (filters.All, filters.UserUnhandled) switch
        {
            (true, _) => "Breaking on every thrown exception, handled or not.",
            (false, true) => "Breaking on unhandled exceptions only.",
            _ => "Not breaking on exceptions at all.",
        };

        if (include.Count > 0)
            scope += $" Thrown exceptions limited to {string.Join(", ", include)}.";
        if (exclude.Count > 0)
            scope += $" Ignoring thrown {string.Join(", ", exclude)}.";
        if (unhandledInclude.Count > 0)
            scope += $" Unhandled limited to {string.Join(", ", unhandledInclude)}.";
        if (unhandledExclude.Count > 0)
            scope += $" Ignoring unhandled {string.Join(", ", unhandledExclude)}.";

        return Task.FromResult(scope);
    }

    public async Task<string> RunToLocationAsync(
        string filePath, int line, CancellationToken cancellationToken = default)
    {
        if (_state == DebuggerService.DebugState.NotStarted)
            return "Error: No debug session is active.";

        var request = new RunToLocationRequest
        {
            Location = new SourceRange { FilePath = filePath, Line = (uint)Math.Max(0, line) },
        };

        // A location in a decompiled or fetched file is translated to IL here, because the
        // engine's document resolution cannot see a file no PDB records.
        var target = await ExternalSource.DebugSourceMapper.TryMapAsync(
            PathHelper.NormalizePath(filePath), line, cancellationToken);
        if (target is not null)
        {
            request.Location.Line = (uint)target.Line;
            request.ModulePath = target.AssemblyPath;
            request.MethodToken = target.MethodToken;
            request.IlOffset = target.IlOffset;
        }

        var response = await Engine.RunToLocationAsync(request);

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

        var request = new SetNextStatementRequest
        {
            FrameIndex = (uint)_selectedFrame,
            Location = new SourceRange { FilePath = filePath, Line = (uint)Math.Max(0, line) },
        };

        // For a decompiled or fetched file the line is translated to an IL offset here; the
        // engine verifies the offset belongs to the selected frame's own method before moving.
        var target = await ExternalSource.DebugSourceMapper.TryMapAsync(
            PathHelper.NormalizePath(filePath), line, cancellationToken);
        if (target is not null)
        {
            request.Location.Line = (uint)target.Line;
            request.MethodToken = target.MethodToken;
            request.IlOffset = target.IlOffset;
        }

        var response = await Engine.SetNextStatementAsync(request);

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
            new ModuleInfo(
                m.Name, m.Path, m.SymbolsLoaded, m.SymbolPath, m.Runtime,
                m.SymbolStatus, m.SymbolOrigin, m.SymbolDetail))];
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
    /// <para>
    /// This is the only route onto the desktop runtime: .NET Framework has no in-process metadata
    /// updater, so the edit has to go through the debugger that is already attached. The engine
    /// enabled EnC JIT flags on every module as it loaded, which is what makes the apply possible
    /// at all — a module JITted without them refuses the change.
    /// </para>
    /// <para>
    /// A .NET target on this engine is refused rather than served. It has an in-process updater and
    /// the agent has already applied the same generation, and a generation applied twice fails the
    /// second time — which would leave every later edit diffing against one the debuggee never
    /// took. The wording is the one the hot reload fan-out reads as a skip rather than a failure.
    /// </para>
    /// </remarks>
    public async Task<(bool Ok, string Error)> ApplyDeltaAsync(
        string assemblyName, byte[] metadata, byte[] il, byte[] pdb,
        string? symbolMap = null,
        CancellationToken cancellationToken = default)
    {
        if (!AppliesDeltas)
        {
            return (false,
                $"{NotADeltaTarget} — a .NET target takes its updates through the in-process " +
                "updater instead.");
        }

        if (_engine is null)
            return (false, "No debug session is attached to this engine.");

        // An edit prefers the target stopped, so a running one is broken into first and resumed
        // afterwards — the user asked to apply an edit, not to be told to go and press pause.
        // It has to be a full Break All rather than a bare suspend: applying immediately after
        // ICorDebugProcess::Stop faults inside ApplyChanges instead of failing. When even the
        // Break All produces no usable stop, the engine is still asked: it queues the delta and
        // applies it at the next real breakpoint instead of losing the edit.
        bool paused = false;
        if (CurrentFrame is null)
        {
            await InterruptAsync(cancellationToken);
            paused = CurrentFrame is not null;
        }

        try
        {
            return await _engine.ApplyDeltaAsync(assemblyName, metadata, il, pdb, symbolMap);
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
    /// <summary>The engine appends the stage to the exception message; these read it back off.</summary>
    private static bool IsUnhandled(string message) =>
        message.EndsWith("(unhandled)", StringComparison.Ordinal);

    private static string StripStage(string message)
    {
        foreach (var suffix in new[] { " (unhandled)", " (first chance)" })
        {
            if (message.EndsWith(suffix, StringComparison.Ordinal))
                return message[..^suffix.Length];
        }
        return message;
    }

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
        sb.AppendLine($"**Engine:** ICorDebug ({RuntimeName})");

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
                                // The engine reports the exception as "Type: message (stage)".
                                ExceptionName: e.Kind == DebugEventKind.Exception
                                    ? ExceptionTypeOf(StripStage(e.Message))
                                    : null,
                                ExceptionMessage: e.Kind == DebugEventKind.Exception
                                    ? StripStage(e.Message)
                                    : null,
                                ExceptionStage: e.Kind == DebugEventKind.Exception
                                    ? (IsUnhandled(e.Message) ? "unhandled" : "throw")
                                    : null,
                                ThreadId: e.ThreadId);
                        }
                        _selectedFrame = 0;
                        _state = DebuggerService.DebugState.Stopped;
                        // The one moment the engine can safely re-mark modules, and the moment a
                        // workspace opened since the attach first matters.
                        RefreshUserAssemblies();
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

                    case DebugEventKind.Logpoint:
                        Remember(e.Message);
                        Raise(DebugNoticeKind.Logpoint, e);
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
        {
            var origin = frame.SourceOrigin.Length > 0 ? $" ({frame.SourceOrigin})" : "";
            sb.AppendLine($"**Location:** {Path.GetFileName(frame.FilePath)}:{frame.Line}{origin}");
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        try { _engine?.Dispose(); } catch { }
        _engine = null;
        try { _stopped.Dispose(); } catch { }
    }
}

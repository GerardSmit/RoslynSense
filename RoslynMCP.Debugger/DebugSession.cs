using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using ClrDebug;

namespace RoslynMCP.Debugger;

/// One ICorDebug debug session over a launched or attached .NET Framework debuggee. Ported from
/// docs/research/probes/IcorDebugProbe. All ICorDebug setup + client commands run on one
/// long-lived session thread (the runtime ties the debugger context to it); managed callbacks
/// arrive on the runtime's own thread and push events. The debuggee stops on every callback; we
/// auto-continue all but breakpoints/step-completes/pauses (which wait for an explicit Continue).
public sealed class DebugSession : IDebugSession
{
    public uint Id { get; }
    public int Pid { get; private set; }

    private readonly Channel<DebugEvent> _events = Channel.CreateUnbounded<DebugEvent>();
    private readonly List<BreakpointSpec> _specs = new();
    private readonly object _specLock = new();
    /// Active bound breakpoints keyed by "file|line" (source) or "type.method" (entry).
    private readonly ConcurrentDictionary<string, CorDebugFunctionBreakpoint> _bound = new();
    /// Source specs keyed by the ACTUAL bound line's SourceKey, for hit-count/condition checks
    /// (the bound line can differ from the requested line when it snapped to a later sequence
    /// point). Hit counters live beside them.
    private readonly ConcurrentDictionary<string, BreakpointSpec> _boundSpecs = new();
    private readonly ConcurrentDictionary<string, int> _hitCounts = new();
    private readonly BlockingCollection<Action> _commands = new();
    private readonly ManualResetEventSlim _ready = new();
    /// PDB readers are expensive to create; one per module path, session-thread only.
    /// Set when an ApplyChanges fails: the runtime and the debugger's metadata view may now
    /// disagree, and there is no rollback, so no further edit is accepted.
    private bool _encPoisoned;

    private readonly Dictionary<string, SymbolReader?> _readers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CorDebugStepper> _steppers = new();
    private CorDebug? _corDebug;
    private CorDebugProcess? _process;
    private CorDebugThread? _stoppedThread;
    private SuspendedProcess? _child;
    private Exception? _launchError;
    private Thread? _thread;
    private DebugRuntime _runtime = DebugRuntime.NetFramework;
    private DbgShim? _dbgShim;
    private IntPtr _dbgShimModule;
    private RuntimeStartupCallback? _coreClrStartupCallback;
    private IntPtr _coreClrStartupCookie;
    /// Non-zero for attach sessions: drives CLR version discovery from the live target.
    private int _attachPid;
    /// Exception stop policy: unhandled exceptions always stop; first-chance stops only when
    /// this is set (default report-and-continue).
    private volatile bool _breakOnFirstChance;
    /// Module that produced each bound breakpoint key, so an unload (app-domain recycle) can
    /// return those breakpoints to pending and let the next LoadModule rebind them.
    private readonly ConcurrentDictionary<string, string> _boundModule = new(StringComparer.OrdinalIgnoreCase);
    /// Non-framework modules by simple assembly name, JIT-flagged for EnC at load — the
    /// ApplyHotReload targets.
    private readonly ConcurrentDictionary<string, CorDebugModule> _encModules = new(StringComparer.OrdinalIgnoreCase);
    /// Modules already reported as symbol-less (one diagnostic per module, not per breakpoint).
    private readonly HashSet<string> _noSymbolsReported = new(StringComparer.OrdinalIgnoreCase);

    public DebugSession(uint id) => Id = id;

    public ChannelReader<DebugEvent> Events => _events.Reader;

    /// <summary>
    /// Launches the debuggee suspended so the debugger can attach before any code runs, and
    /// forwards its console output as Output events.
    /// </summary>
    private SuspendedProcess StartSuspended(
        string commandLine, string workingDirectory, IReadOnlyDictionary<string, string>? env)
    {
        var child = SuspendedProcess.Start(commandLine, workingDirectory, env);
        child.OutputReceived += line => Emit(DebugEventKind.Output, line, string.Empty, 0);
        return child;
    }

    /// A launched executable is .NET Framework when it has no `<name>.runtimeconfig.json` (which
    /// only .NET Core/5+ apps emit). The legacy ICorDebug shim path only handles .NET Framework.
    public static bool IsNetFramework(string exePath)
        => !File.Exists(Path.ChangeExtension(exePath, ".runtimeconfig.json"));

    public void Launch(
        string executable, IReadOnlyList<string> args, IEnumerable<BreakpointSpec> breakpoints,
        IReadOnlyDictionary<string, string>? env = null, string? workingDirectory = null,
        DebugRuntime runtime = DebugRuntime.NetFramework)
    {
        lock (_specLock)
            _specs.AddRange(breakpoints);
        var exe = Path.GetFullPath(executable);
        var argv = args.ToArray();
        _runtime = runtime == DebugRuntime.Unspecified ? DebugRuntime.NetFramework : runtime;
        StartSessionThread(callback => AttachCore(exe, argv, attachPid: 0, callback, env, workingDirectory));
    }

    /// Attach to a running .NET Framework process (IIS Express, w3wp) by pid.
    public void Attach(
        int pid,
        IEnumerable<BreakpointSpec> breakpoints,
        DebugRuntime runtime = DebugRuntime.NetFramework)
    {
        lock (_specLock)
            _specs.AddRange(breakpoints);
        _runtime = runtime == DebugRuntime.Unspecified ? DebugRuntime.NetFramework : runtime;
        _attachPid = pid;
        StartSessionThread(callback => AttachCore(exe: null, args: null, attachPid: pid, callback));
    }

    private void StartSessionThread(Action<CorDebugManagedCallback> setup)
    {
        _thread = new Thread(() => RunSession(setup)) { IsBackground = true, Name = $"debug-{Id}" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        _ready.Wait();
        if (_launchError is not null)
            throw _launchError;
    }

    // --- client commands (all marshalled onto the session thread) ------------------------------

    public void AddBreakpoint(BreakpointSpec spec)
    {
        lock (_specLock)
            _specs.Add(spec);
        // Try binding into already-loaded modules; stopping the process is required to touch
        // ICorDebug state, so ride the command queue while stopped or bind lazily on next stop.
        Enqueue(() =>
        {
            if (_process is null || _stoppedThread is null)
                return; // running: LoadModule/next-stop rebind picks it up
            foreach (var module in LoadedModules())
                TryBindBreakpoint(module, spec);
        });
    }

    public bool RemoveBreakpoint(string filePath, int line)
    {
        var key = SourceKey(filePath, line);
        lock (_specLock)
            _specs.RemoveAll(s => s.FilePath.Length > 0 && SourceKey(s.FilePath, (int)s.Line) == key);
        if (!_bound.TryRemove(key, out var breakpoint))
            return false;
        Enqueue(() => { try { breakpoint.Activate(false); } catch { } });
        return true;
    }

    public Task<BreakpointLocationsResponse> BreakpointLocationsAsync(BreakpointLocationsRequest request)
        => InvokeAsync(() =>
        {
            var response = new BreakpointLocationsResponse();
            if (request.FilePath.Length == 0 || request.Line == 0)
                return response;
            foreach (var module in LoadedModules())
                response.Locations.AddRange(BreakpointLocationsInModule(module, request));
            DeduplicateLocations(response.Locations);
            return response;
        });

    public Task<RunToLocationResponse> RunToLocationAsync(RunToLocationRequest request)
        => InvokeAsync(() =>
        {
            var location = request.Location;
            if (location is null || location.FilePath.Length == 0 || location.Line == 0)
                return new RunToLocationResponse { Ok = false, Error = "missing source location" };
            var thread = _stoppedThread;
            if (thread is null)
                return new RunToLocationResponse { Ok = false, Error = "debuggee is not stopped" };
            var resolved = ResolveBestLocation(location.FilePath, (int)location.Line, (int)location.Column);
            if (resolved is null)
                return new RunToLocationResponse { Ok = false, Error = "no executable location found" };

            var spec = new BreakpointSpec
            {
                Id = $"run-to:{Guid.NewGuid():N}",
                FilePath = resolved.Actual!.FilePath,
                Line = resolved.Actual.Line,
                Column = resolved.Actual.Column,
                EndLine = resolved.Actual.EndLine,
                EndColumn = resolved.Actual.EndColumn,
                Temporary = true,
                Kind = BreakpointKind.Source,
            };
            lock (_specLock)
                _specs.Add(spec);
            foreach (var module in LoadedModules())
                TryBindBreakpoint(module, spec);
            _stoppedThread = null;
            try { _process?.Continue(false); } catch { }
            return new RunToLocationResponse { Ok = true, Location = resolved };
        });

    public Task<SetNextStatementResponse> SetNextStatementAsync(SetNextStatementRequest request)
        => InvokeAsync(() =>
        {
            var location = request.Location;
            if (location is null || location.FilePath.Length == 0 || location.Line == 0)
                return new SetNextStatementResponse { Ok = false, Error = "missing source location" };
            var thread = _stoppedThread;
            if (thread is null)
                return new SetNextStatementResponse { Ok = false, Error = "debuggee is not stopped" };
            var frame = FrameAt(thread, request.FrameIndex) as CorDebugILFrame;
            if (frame is null)
                return new SetNextStatementResponse { Ok = false, Error = "no managed IL frame selected" };
            var target = ResolveBestLocationInFrame(frame, location.FilePath, (int)location.Line, (int)location.Column);
            if (target is null)
                return new SetNextStatementResponse
                {
                    Ok = false,
                    Error = "target is not a legal executable point in the selected method",
                };
            try
            {
                var hr = frame.TryCanSetIP(target.Value.Offset);
                if (hr != HRESULT.S_OK)
                    return new SetNextStatementResponse { Ok = false, Error = $"Set Next Statement rejected: {hr}" };
                frame.SetIP(target.Value.Offset);
                var actual = target.Value.Location.Actual?.Clone();
                if (actual is not null)
                    Emit(
                        DebugEventKind.Paused,
                        "set next statement",
                        DescribeMethod(frame),
                        ThreadId(thread),
                        actual.FilePath,
                        (int)actual.Line,
                        (int)actual.Column,
                        actualLocation: actual);
                return new SetNextStatementResponse { Ok = true, Actual = actual };
            }
            catch (Exception ex)
            {
                return new SetNextStatementResponse { Ok = false, Error = ex.Message };
            }
        });

    public void Continue() => Enqueue(() =>
    {
        _stoppedThread = null;
        try { _process?.Continue(false); } catch { }
    });

    public void Pause() => Enqueue(() =>
    {
        try
        {
            _process?.Stop(0);
            _stoppedThread = null;
            Emit(DebugEventKind.Paused, "paused", string.Empty, 0);
        }
        catch { }
    });

    /// First-chance exception stop policy (unhandled exceptions always stop).
    public void SetExceptionPolicy(bool breakOnFirstChance) => _breakOnFirstChance = breakOnFirstChance;

    public void Step(StepKind kind) => Enqueue(() =>
    {
        var thread = _stoppedThread;
        if (thread is null)
            return;
        try
        {
            var frame = thread.ActiveFrame;
            var stepper = frame.CreateStepper();
            stepper.SetInterceptMask(CorDebugIntercept.INTERCEPT_NONE);
            stepper.SetUnmappedStopMask(CorDebugUnmappedStop.STOP_NONE);
            var source = frame is CorDebugILFrame ilFrame && TryGetSourceStepRange(ilFrame, out var range)
                ? (COR_DEBUG_STEP_RANGE?)range
                : null;
            switch (kind)
            {
                case StepKind.Out:
                    stepper.StepOut();
                    break;
                case StepKind.Into when source is { } r:
                    stepper.SetRangeIL(true);
                    stepper.StepRange(true, [r], 1);
                    break;
                case StepKind.Into:
                    stepper.Step(true);
                    break;
                case StepKind.Over when source is { } r:
                    stepper.SetRangeIL(true);
                    stepper.StepRange(false, [r], 1);
                    break;
                default:
                    stepper.Step(false);
                    break;
            }
            _steppers.Add(stepper);
            _stoppedThread = null;
            _process?.Continue(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[debug] step failed: {ex.Message}");
        }
    });

    /// The stopped thread's call stack, top-first. Empty when running.
    public Task<List<StackFrame>> StackTraceAsync() => InvokeAsync(() =>
    {
        var frames = new List<StackFrame>();
        var thread = _stoppedThread;
        if (thread is null)
            return frames;
        var index = 0u;
        foreach (var chain in Safe(() => thread.Chains) ?? Array.Empty<CorDebugChain>())
        {
            foreach (var frame in Safe(() => chain.Frames) ?? Array.Empty<CorDebugFrame>())
            {
                var (file, line, column) = FrameLocation(frame);
                frames.Add(new StackFrame
                {
                    Index = index++,
                    Method = DescribeMethod(frame),
                    FilePath = file,
                    Line = (uint)line,
                    Column = (uint)column,
                    ThreadId = ThreadId(thread),
                });
                if (index >= 128)
                    return frames;
            }
        }
        return frames;
    });

    public Task<List<DebugThread>> ThreadsAsync() => InvokeAsync(() =>
    {
        var threads = new List<DebugThread>();
        var process = _process;
        if (process is null)
            return threads;
        foreach (var appDomain in Safe(() => process.AppDomains) ?? Array.Empty<CorDebugAppDomain>())
        {
            foreach (var thread in Safe(() => appDomain.Threads) ?? Array.Empty<CorDebugThread>())
            {
                var (file, line, _) = ThreadLocation(thread);
                threads.Add(new DebugThread
                {
                    Id = ThreadId(thread),
                    Stopped = _stoppedThread is not null && ThreadId(thread) == ThreadId(_stoppedThread),
                    Location = file.Length > 0 && line > 0
                        ? $"{Path.GetFileName(file)}:{line}"
                        : MethodOf(thread),
                });
            }
        }
        return threads;
    });

    public Task<List<DebugModule>> ModulesAsync() => InvokeAsync(() =>
    {
        var modules = new List<DebugModule>();
        foreach (var module in LoadedModules())
        {
            var path = Safe(() => module.Name) ?? string.Empty;
            if (path.Length == 0)
                continue;
            var reader = ReaderFor(module, path);
            modules.Add(new DebugModule
            {
                Name = Path.GetFileName(path),
                Path = path,
                SymbolsLoaded = reader is not null,
                SymbolPath = reader is null ? string.Empty : Path.ChangeExtension(path, ".pdb"),
                Runtime = _runtime == DebugRuntime.CoreClr ? ".NET" : ".NET Framework",
            });
        }
        return modules;
    });

    /// Arguments + locals for stack frame `frameIndex` of the stopped thread.
    public Task<List<DebugVariable>> VariablesAsync(uint frameIndex) => InvokeAsync(() =>
    {
        var variables = new List<DebugVariable>();
        var thread = _stoppedThread;
        if (thread is null)
            return variables;
        var frame = FrameAt(thread, frameIndex);
        if (frame is not CorDebugILFrame ilFrame)
            return variables;

        var (argNames, localNames) = FrameSymbolNames(ilFrame);
        AppendValues(variables, "arg", Safe(() => ilFrame.Arguments), argNames);
        AppendValues(variables, "local", Safe(() => ilFrame.LocalVariables), localNames);
        return variables;
    });

    public Task<List<DebugScope>> ScopesAsync(uint frameIndex) => InvokeAsync(() =>
    {
        var scopes = new List<DebugScope>();
        var thread = _stoppedThread;
        if (thread is null)
            return scopes;
        if (FrameAt(thread, frameIndex) is CorDebugILFrame)
            scopes.Add(new DebugScope { Name = "Locals", VariablesReference = $"frame:{frameIndex}:locals" });
        return scopes;
    });

    /// <summary>
    /// Assigns a new value to an argument, local, or field reachable by a dotted path.
    /// </summary>
    /// <remarks>
    /// Limited to primitives and strings written as literals. Only values with a raw
    /// representation can be written directly; assigning an object would mean constructing one in
    /// the debuggee.
    /// </remarks>
    public Task<(bool Ok, DebugVariable? Variable, string Error)> SetVariableAsync(
        uint frameIndex, string name, string value)
        => InvokeAsync<(bool Ok, DebugVariable? Variable, string Error)>(() =>
        {
            var thread = _stoppedThread;
            if (thread is null)
                return (false, null, "not stopped");
            if (FrameAt(thread, frameIndex) is not CorDebugILFrame ilFrame)
                return (false, null, "no managed frame");

            var target = ResolvePath(ilFrame, name, out var error);
            if (target is null)
                return (false, null, error);

            if (!TryWriteScalar(target, value, out var writeError))
                return (false, null, writeError);

            return (true, new DebugVariable
            {
                Name = name,
                Value = DescribeValue(target),
                Kind = "local",
                Settable = true,
            }, string.Empty);
        });

    /// <summary>
    /// Parses <paramref name="literal"/> according to the target's element type and writes it.
    /// </summary>
    private static bool TryWriteScalar(CorDebugValue target, string literal, out string error)
    {
        error = string.Empty;

        if (target is not CorDebugGenericValue generic)
        {
            error = "only primitive and boolean values can be assigned";
            return false;
        }

        var type = Safe(() => (CorElementType?)target.Type);
        if (type is null)
        {
            error = "could not determine the value's type";
            return false;
        }

        try
        {
            object parsed = type switch
            {
                CorElementType.Boolean => bool.Parse(literal),
                CorElementType.Char => literal.Length > 0 ? literal[0] : '\0',
                CorElementType.I1 => sbyte.Parse(literal),
                CorElementType.U1 => byte.Parse(literal),
                CorElementType.I2 => short.Parse(literal),
                CorElementType.U2 => ushort.Parse(literal),
                CorElementType.I4 => int.Parse(literal),
                CorElementType.U4 => uint.Parse(literal),
                CorElementType.I8 => long.Parse(literal),
                CorElementType.U8 => ulong.Parse(literal),
                CorElementType.R4 => float.Parse(literal),
                CorElementType.R8 => double.Parse(literal),
                _ => throw new NotSupportedException($"values of type {type} cannot be assigned"),
            };

            var handle = System.Runtime.InteropServices.GCHandle.Alloc(parsed, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                generic.SetValue(handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex is FormatException
                ? $"'{literal}' is not a valid {type} value"
                : ex.Message;
            return false;
        }
    }

    /// Evaluate a watch expression (dotted member path rooted at an argument/local, with array
    /// indexers) in stack frame `frameIndex`. No func-eval: fields and auto-property backing
    /// fields resolve, computed properties do not.
    public Task<(bool Ok, string Value, string Error)> EvaluateAsync(uint frameIndex, string expression)
        => InvokeAsync(() =>
        {
            var thread = _stoppedThread;
            if (thread is null)
                return (false, string.Empty, "not stopped");
            var frame = FrameAt(thread, frameIndex);
            if (frame is not CorDebugILFrame ilFrame)
                return (false, string.Empty, "no managed frame");
            var value = ResolvePath(ilFrame, expression, out var error);
            return value is null
                ? (false, string.Empty, error)
                : (true, DescribeValue(value), string.Empty);
        });

    /// Hit-count / condition gate for a source breakpoint stop. Unknown locations (entry
    /// breakpoints, steps) always stop; a condition that fails to evaluate stops too (fail-open,
    /// so a typo never silently swallows the breakpoint).
    private bool ShouldStopAt(CorDebugThread thread, string file, int line)
    {
        if (file.Length == 0 || _boundSpecs.IsEmpty)
            return true;
        if (!_boundSpecs.TryGetValue(SourceKey(file, line), out var spec))
            return true;

        var hits = _hitCounts.AddOrUpdate(SourceKey(file, line), 1, (_, c) => c + 1);
        if (hits <= spec.SkipHits)
            return false;

        if (spec.Condition.Length == 0)
            return true;
        try
        {
            return EvaluateCondition(thread, spec.Condition);
        }
        catch
        {
            return true;
        }
    }

    /// "path == literal" / "path != literal" compared against the stringified value; a bare path
    /// is truthy when it isn't null/false/0.
    private bool EvaluateCondition(CorDebugThread thread, string condition)
    {
        if (thread.ActiveFrame is not CorDebugILFrame ilFrame)
            return true;

        string path;
        string? expected = null;
        var negate = false;
        var eq = condition.IndexOf("==", StringComparison.Ordinal);
        var ne = condition.IndexOf("!=", StringComparison.Ordinal);
        if (eq >= 0)
        {
            path = condition[..eq].Trim();
            expected = condition[(eq + 2)..].Trim();
        }
        else if (ne >= 0)
        {
            path = condition[..ne].Trim();
            expected = condition[(ne + 2)..].Trim();
            negate = true;
        }
        else
        {
            path = condition.Trim();
        }

        var value = ResolvePath(ilFrame, path, out _);
        if (value is null)
            return true; // unresolvable → fail-open
        var actual = DescribeValue(value);
        if (expected is null)
            return actual is not ("null" or "False" or "false" or "0");

        var want = expected.Trim('"');
        var got = actual.Trim('"');
        var equal = string.Equals(got, want, StringComparison.Ordinal);
        return negate ? !equal : equal;
    }

    /// Resolve `expr` ("order.Customer.Name", "items[3].Id", "this.Count") against a frame's
    /// arguments/locals by walking object fields and array elements.
    private CorDebugValue? ResolvePath(CorDebugILFrame ilFrame, string expr, out string error)
    {
        error = string.Empty;
        var segments = expr.Replace(" ", string.Empty)
            .Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            error = "empty expression";
            return null;
        }

        var (argNames, localNames) = FrameSymbolNames(ilFrame);
        CorDebugValue? current = null;
        for (var s = 0; s < segments.Length; s++)
        {
            var (name, indexes, isCall) = ParseSegment(segments[s]);
            if (s == 0)
            {
                current = isCall ? null : RootValue(ilFrame, name, argNames, localNames);

                // Not an argument or local: it may be a member of `this`, which is how a member is
                // normally written inside an instance method.
                if (current is null && RootValue(ilFrame, "this", argNames, localNames) is { } self)
                    current = MemberValue(self, name, isCall, out error);

                if (current is null)
                {
                    if (error.Length == 0)
                        error = $"'{name}' is not an argument, local, or member of this";
                    return null;
                }

                error = string.Empty;
            }
            else
            {
                current = MemberValue(current!, name, isCall, out error);
                if (current is null)
                    return null;
            }

            foreach (var index in indexes)
            {
                current = ElementValue(current, index);
                if (current is null)
                {
                    error = $"cannot index into '{name}'";
                    return null;
                }
            }
        }
        return current;
    }

    /// <summary>
    /// Splits one path segment into its member name, any array indexers, and whether it was
    /// written as a call — <c>Items[0]</c>, <c>Count</c>, <c>ToString()</c>.
    /// </summary>
    private static (string Name, List<int> Indexes, bool IsCall) ParseSegment(string segment)
    {
        // Only parameterless calls are supported; arguments would need to be evaluated too.
        var isCall = segment.EndsWith("()", StringComparison.Ordinal);
        if (isCall)
            segment = segment[..^2];

        var indexes = new List<int>();
        var bracket = segment.IndexOf('[');
        var name = bracket < 0 ? segment : segment[..bracket];
        while (bracket >= 0)
        {
            var close = segment.IndexOf(']', bracket);
            if (close < 0)
                break;
            if (int.TryParse(segment[(bracket + 1)..close], out var idx))
                indexes.Add(idx);
            bracket = segment.IndexOf('[', close);
        }
        return (name, indexes, isCall);
    }

    private static CorDebugValue? RootValue(
        CorDebugILFrame ilFrame, string name,
        Dictionary<int, string> argNames, Dictionary<int, string> localNames)
    {
        var args = Safe(() => ilFrame.Arguments);
        if (args is not null)
        {
            if (name == "this" && args.Length > 0 && !argNames.ContainsValue("this") && !argNames.ContainsKey(0))
                return args[0]; // instance methods: arg0 is the unnamed `this`
            for (var i = 0; i < args.Length; i++)
            {
                if (argNames.TryGetValue(i, out var n) && n == name)
                    return args[i];
            }
        }
        var locals = Safe(() => ilFrame.LocalVariables);
        if (locals is not null)
        {
            for (var i = 0; i < locals.Length; i++)
            {
                if (localNames.TryGetValue(i, out var n) && n == name)
                    return locals[i];
            }
        }
        return null;
    }

    /// A named field (or auto-property backing field) of an object value, searching the class
    /// hierarchy within the declaring module.
    private static CorDebugValue? FieldValue(CorDebugValue value, string name)
    {
        var target = Safe(() => Dereference(value));
        if (target is not CorDebugObjectValue obj)
            return null;
        var cls = Safe(() => obj.Class);
        for (var depth = 0; cls is not null && depth < 16; depth++)
        {
            var module = Safe(() => cls.Module);
            if (module is null)
                return null;
            var metadata = Safe(() => Extensions.GetMetaDataInterface<MetaDataImport>(module));
            if (metadata is null)
                return null;

            foreach (var candidate in new[] { name, $"<{name}>k__BackingField" })
            {
                var token = Safe(() =>
                {
                    var field = metadata.FindField(cls.Token, candidate, IntPtr.Zero, 0);
                    return (mdFieldDef?)field;
                });
                if (token is { } fieldToken)
                {
                    var result = Safe(() => obj.GetFieldValue(cls!.Raw, fieldToken));
                    if (result is not null)
                        return result;
                }
            }

            // Walk to the base type when it lives in the same module (TypeDef extends only).
            var currentCls = cls;
            cls = Safe(() =>
            {
                var props = metadata.GetTypeDefProps(currentCls.Token);
                var extends = props.ptkExtends;
                return extends.Type == CorTokenType.mdtTypeDef && extends.Rid != 0
                    ? module.GetClassFromToken((mdTypeDef)extends)
                    : null;
            });
        }
        return null;
    }

    // --- function evaluation ------------------------------------------------------------------

    /// <summary>
    /// How long to let an evaluated member run before abandoning it. A property getter that blocks
    /// would otherwise hang the session, since the debuggee must be resumed for the eval to run.
    /// </summary>
    private static readonly TimeSpan EvalTimeout = TimeSpan.FromSeconds(10);

    private CorDebugEval? _pendingEval;
    private CorDebugValue? _evalResult;
    private bool _evalFaulted;
    private ManualResetEventSlim? _evalDone;

    /// <summary>
    /// Runs on the runtime's callback thread when an evaluation finishes, handing the result to
    /// the caller blocked in <see cref="InvokeFunction"/>.
    /// </summary>
    private void CompleteEval(CorDebugEval eval, bool faulted)
    {
        if (_pendingEval is null)
            return;

        _evalFaulted = faulted;
        _evalResult = faulted ? null : Safe(() => eval.Result);
        _evalDone?.Set();
    }

    /// <summary>
    /// Calls a method in the debuggee and returns its result.
    /// </summary>
    /// <remarks>
    /// An evaluation only runs while the debuggee is running, so this resumes the process and
    /// blocks until the completion callback fires. That is safe on the session thread because
    /// callbacks arrive on the runtime's own thread. The debuggee is left stopped exactly where it
    /// was, because the callback is excluded from the auto-continue in <c>OnAnyEvent</c>.
    /// </remarks>
    private CorDebugValue? InvokeFunction(CorDebugFunction function, CorDebugValue[] args, out string error)
    {
        error = string.Empty;

        var thread = _stoppedThread;
        var process = _process;
        if (thread is null || process is null)
        {
            error = "not stopped";
            return null;
        }

        CorDebugEval eval;
        try
        {
            eval = thread.CreateEval();
        }
        catch (Exception ex)
        {
            error = $"could not start an evaluation: {ex.Message}";
            return null;
        }

        using var done = new ManualResetEventSlim(false);
        _pendingEval = eval;
        _evalDone = done;
        _evalResult = null;
        _evalFaulted = false;

        try
        {
            eval.CallFunction(function.Raw, args.Length, args.Select(a => a.Raw).ToArray());
            process.Continue(false);

            if (!done.Wait(EvalTimeout))
            {
                try { eval.Abort(); } catch { }
                error = "the evaluation timed out";
                return null;
            }

            if (_evalFaulted)
            {
                error = "the evaluated member threw an exception";
                return null;
            }

            return _evalResult;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
        finally
        {
            _pendingEval = null;
            _evalDone = null;
        }
    }

    /// <summary>
    /// Finds a method by name on a value's type, walking base types within the declaring module.
    /// </summary>
    private static (CorDebugFunction Function, CorDebugValue Instance)? FindMethod(
        CorDebugValue value, string name)
    {
        // The dereferenced object is what carries the class, but the instance argument handed to
        // CallFunction must stay the original reference — passing the dereferenced object makes
        // the runtime fault the evaluation.
        var target = Safe(() => Dereference(value));
        if (target is not CorDebugObjectValue obj)
            return null;

        var cls = Safe(() => obj.Class);
        for (var depth = 0; cls is not null && depth < 16; depth++)
        {
            var module = Safe(() => cls.Module);
            if (module is null)
                return null;

            var metadata = Safe(() => Extensions.GetMetaDataInterface<MetaDataImport>(module));
            if (metadata is null)
                return null;

            var token = Safe(() =>
            {
                var method = metadata.FindMethod(cls.Token, name, IntPtr.Zero, 0);
                return (mdMethodDef?)method;
            });

            if (token is { } methodToken)
            {
                var function = Safe(() => module.GetFunctionFromToken(methodToken));
                if (function is not null)
                    return (function, value);
            }

            var currentCls = cls;
            cls = Safe(() =>
            {
                var props = metadata.GetTypeDefProps(currentCls.Token);
                var extends = props.ptkExtends;
                return extends.Type == CorTokenType.mdtTypeDef && extends.Rid != 0
                    ? module.GetClassFromToken((mdTypeDef)extends)
                    : null;
            });
        }

        return null;
    }

    /// <summary>
    /// Reads a member: a field first (cheapest, and cannot have side effects), then a property
    /// getter or parameterless method through function evaluation.
    /// </summary>
    private CorDebugValue? MemberValue(CorDebugValue value, string name, bool callOnly, out string error)
    {
        error = string.Empty;

        if (!callOnly && FieldValue(value, name) is { } field)
            return field;

        // A computed property has no backing field, so it has to be invoked.
        foreach (var candidate in callOnly ? [name] : new[] { $"get_{name}", name })
        {
            if (FindMethod(value, candidate) is not { } found)
                continue;

            var result = InvokeFunction(found.Function, [found.Instance], out error);
            if (result is not null)
                return result;

            // The member exists but the call failed; report that rather than "not found".
            if (error.Length > 0)
                return null;
        }

        error = callOnly
            ? $"no parameterless method '{name}' was found"
            : $"member '{name}' was not found";
        return null;
    }

    private static CorDebugValue? ElementValue(CorDebugValue value, int index)
    {
        var target = Safe(() => Dereference(value));
        return target is CorDebugArrayValue array
            ? Safe(() => array.GetElementAtPosition(index))
            : null;
    }

    private static CorDebugValue Dereference(CorDebugValue value)
    {
        if (value is CorDebugReferenceValue reference && Safe(() => (bool?)reference.IsNull) != true)
            return Safe(() => reference.Dereference()) ?? value;
        return value;
    }

    public void Terminate()
    {
        Enqueue(() =>
        {
            var process = _process;
            try { process?.Terminate(0); } catch { }

            // A session almost always ends while stopped at a breakpoint. Terminating a stopped
            // process only queues the kill: the runtime still needs a Continue to drain its exit
            // callbacks, and without it the debugging interface stays wedged and the next attach
            // in this process receives no callbacks at all.
            try { process?.Continue(false); } catch { }

            ShutdownCorDebug();
        });

        _events.Writer.TryComplete();
        try { _commands.CompleteAdding(); } catch { }

        // Wait for the session thread to drain, so a subsequent session starts against a fully
        // released debugging interface rather than racing this one's teardown.
        var thread = _thread;
        if (thread is not null && !ReferenceEquals(thread, Thread.CurrentThread))
        {
            try { thread.Join(TimeSpan.FromSeconds(5)); } catch { }
        }
    }

    /// <summary>
    /// Releases the ICorDebug interface.
    /// </summary>
    /// <remarks>
    /// Required, not merely tidy: the desktop CLR hands out its debugging interface per runtime,
    /// so a session that never terminated leaves the interface live and the *next*
    /// <c>Initialize</c>/<c>SetManagedHandler</c> in the same process silently fails to deliver
    /// callbacks — breakpoints then never bind for any later session.
    /// </remarks>
    private void ShutdownCorDebug()
    {
        var corDebug = _corDebug;
        _corDebug = null;
        _process = null;
        _stoppedThread = null;

        if (corDebug is null)
            return;

        try { corDebug.Terminate(); } catch { }
    }

    /// Detach without killing the debuggee — the safe teardown for attached IIS Express / w3wp
    /// workers. Per the ICorDebug probe: the process must be synchronized (Stop) and every bound
    /// breakpoint deactivated first, or Detach fails with CORDBG_E_PROCESS_NOT_SYNCHRONIZED /
    /// CORDBG_E_DETACH_FAILED_OUTSTANDING_BREAKPOINTS.
    public Task<(bool Ok, string Error)> DetachAsync() => InvokeAsync<(bool Ok, string Error)>(() =>
    {
        var process = _process;
        if (process is null)
            return (false, "no process");
        try
        {
            try { process.Stop(5000); } catch { }
            foreach (var breakpoint in _bound.Values)
            {
                try { breakpoint?.Activate(false); } catch { }
            }
            _bound.Clear();
            _boundSpecs.Clear();
            _steppers.Clear();
            _stoppedThread = null;
            process.Detach();
            _process = null;

            // Detaching leaves the debuggee running but this session is over, so release the
            // debugging interface too — otherwise the next session in this process gets no
            // callbacks (see ShutdownCorDebug).
            ShutdownCorDebug();
            Emit(DebugEventKind.Exited, "detached", string.Empty, 0);
            _events.Writer.TryComplete();
            try { _commands.CompleteAdding(); } catch { }
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    });

    /// Refuse cross-bitness attach up front with an actionable message — ICorDebug's failure is
    /// an opaque HRESULT otherwise. (x86 worker support needs a 32-bit companion process; see
    /// NETFX.md M4.)
    private static void EnsureAttachArchitecture(int pid)
    {
        if (!Environment.Is64BitProcess || !OperatingSystem.IsWindows())
            return;
        try
        {
            using var target = System.Diagnostics.Process.GetProcessById(pid);
            if (IsWow64Process(target.Handle, out var wow64) && wow64)
                throw new InvalidOperationException(
                    $"pid {pid} is a 32-bit process; this debugger host is x64 and can only attach to "
                    + "x64 targets — use a 64-bit app pool / the 64-bit iisexpress.exe");
        }
        catch (ArgumentException)
        {
            // Process already gone: DebugActiveProcess reports the real error.
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(IntPtr hProcess, out bool wow64Process);

    /// CLR version string for Extensions.GetRuntime: the attached target's actual loaded runtime
    /// when discoverable, else the desktop 4.x default.
    private static string RuntimeVersion(CLRMetaHost metaHost, int attachPid)
    {
        if (attachPid != 0)
        {
            try
            {
                using var target = System.Diagnostics.Process.GetProcessById(attachPid);
                foreach (var item in metaHost.EnumerateLoadedRuntimes(target.Handle))
                {
                    if (item is not ICLRRuntimeInfo raw)
                        continue;
                    var version = new CLRRuntimeInfo(raw).VersionString;
                    if (!string.IsNullOrEmpty(version))
                        return version;
                }
            }
            catch
            {
                // No CLR loaded yet / access denied: fall through to the default.
            }
        }
        return "v4.0.30319";
    }

    private void Enqueue(Action action)
    {
        try { _commands.Add(action); }
        catch (InvalidOperationException) { /* completed */ }
    }

    private Task<T> InvokeAsync<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Without a session thread nothing drains the command queue, so the caller would wait for
        // a result that can never arrive. Fail fast instead of hanging.
        if (_thread is null)
        {
            tcs.SetException(new InvalidOperationException(
                "no debug session has been started; launch or attach first"));
            return tcs.Task;
        }

        Enqueue(() =>
        {
            try { tcs.SetResult(f()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    // --- session thread -------------------------------------------------------------------------

    private void RunSession(Action<CorDebugManagedCallback> setup)
    {
        try
        {
            var callback = new CorDebugManagedCallback();
            callback.OnLoadModule += (_, e) =>
            {
                // Diagnostic visibility for WebForms shadow-copy / App_Web_* module loads.
                var moduleName = Safe(() => e.Module.Name) ?? string.Empty;
                if (moduleName.Length > 0)
                    Emit(DebugEventKind.Module, moduleName, string.Empty, 0);
                RegisterEncModule(e.Module, moduleName);
                foreach (var spec in SpecsSnapshot())
                    TryBindBreakpoint(e.Module, spec);
                ReportMissingSymbols(e.Module, moduleName);
            };
            callback.OnUnloadModule += (_, e) =>
            {
                // App-domain recycle / plugin unload: bound breakpoints in this module go back
                // to pending; the specs stay, so the next LoadModule rebinds them.
                var moduleName = Safe(() => e.Module.Name) ?? string.Empty;
                if (moduleName.Length > 0)
                    OnModuleUnloaded(moduleName);
            };
            callback.OnBreakpoint += (_, e) =>
            {
                var (file, line, column) = ThreadLocation(e.Thread);
                if (!ShouldStopAt(e.Thread, file, line))
                {
                    // Skipped by hit count / condition: resume before OnAnyEvent sees the stop.
                    try { e.Controller.Continue(false); } catch { }
                    return;
                }
                if (TryGetBoundSpec(file, line, out var spec) && spec.Temporary)
                    RemoveBreakpoint(file, line);
                _stoppedThread = e.Thread;
                Emit(DebugEventKind.Breakpoint, "breakpoint hit", MethodOf(e.Thread), ThreadId(e.Thread), file, line, column);
            };
            callback.OnStepComplete += (_, e) =>
            {
                _stoppedThread = e.Thread;
                var (file, line, column) = ThreadLocation(e.Thread);
                Emit(DebugEventKind.Step, "step", MethodOf(e.Thread), ThreadId(e.Thread), file, line, column);
            };
            callback.OnException += (_, e) =>
            {
                // Unhandled (second-chance) exceptions always stop; first-chance stops only
                // under the session policy. Auto-continued first-chance exceptions are reported
                // as Output so the UI never treats a running process as stopped.
                var unhandled = e.Unhandled != 0;
                if (unhandled || _breakOnFirstChance)
                {
                    _stoppedThread = e.Thread;
                    var (file, line, column) = ThreadLocation(e.Thread);
                    Emit(
                        DebugEventKind.Exception,
                        unhandled ? "exception (unhandled)" : "exception (first chance)",
                        MethodOf(e.Thread), ThreadId(e.Thread), file, line, column);
                }
                else
                {
                    Emit(DebugEventKind.Output, $"first-chance exception in {MethodOf(e.Thread)}", string.Empty, 0);
                }
            };
            callback.OnExitProcess += (_, _) =>
            {
                Emit(DebugEventKind.Exited, "process exited", string.Empty, 0);
                _events.Writer.TryComplete();
                try { _commands.CompleteAdding(); } catch { }
            };
            // A func-eval finishes by raising EvalComplete (or EvalException) on the runtime's own
            // thread. Both leave the process stopped, exactly where it was before the eval ran,
            // and hand the waiting caller its result.
            callback.OnEvalComplete += (_, e) => CompleteEval(e.Eval, faulted: false);
            callback.OnEvalException += (_, e) => CompleteEval(e.Eval, faulted: true);

            callback.OnAnyEvent += (_, e) =>
            {
                // Breakpoints + step completes stay stopped (wait for an explicit Continue);
                // so do exceptions the policy decided to stop on (OnException runs before this
                // catch-all and marks the stop by setting _stoppedThread). Resume everything else.
                if (e.Kind is CorDebugManagedCallbackKind.Breakpoint or CorDebugManagedCallbackKind.StepComplete)
                    return;
                if (e.Kind is CorDebugManagedCallbackKind.Exception && _stoppedThread is not null)
                    return;

                // An eval's completion must leave the debuggee stopped: resuming here would run
                // the target on past the point the user is inspecting.
                if (e.Kind is CorDebugManagedCallbackKind.EvalComplete or CorDebugManagedCallbackKind.EvalException)
                    return;

                try { e.Controller.Continue(false); }
                catch { /* already continued / terminated */ }
            };

            if (_runtime == DebugRuntime.CoreClr)
            {
                setup(callback);
            }
            else
            {
                InitializeDesktopRuntime(callback);
                setup(callback);
            }
        }
        catch (Exception ex)
        {
            _launchError = ex;
            Console.Error.WriteLine($"[debug] launch error: {ex}");
            _ready.Set();
            return;
        }

        _ready.Set();

        // Keep this thread alive so the runtime's debugger context survives; run client commands.
        try
        {
            foreach (var command in _commands.GetConsumingEnumerable())
            {
                try { command(); }
                catch (Exception ex) { Console.Error.WriteLine($"[debug] command failed: {ex.Message}"); }
            }
        }
        catch (InvalidOperationException)
        {
            // collection completed
        }
    }

    /// <summary>
    /// The desktop CLR's debugging interface, created once for the lifetime of this process.
    /// </summary>
    /// <remarks>
    /// The shim hands out one interface per runtime, so re-creating it per session does not give a
    /// fresh one: the second session's <c>Initialize</c> appears to succeed but no managed
    /// callbacks are ever delivered, and every breakpoint binds yet never fires. Creating it once
    /// and re-pointing the handler at each new session is what actually works. It is deliberately
    /// never terminated — terminating it would break every later session in the same process.
    /// </remarks>
    private static readonly Lock s_desktopLock = new();

    private void InitializeDesktopRuntime(CorDebugManagedCallback callback)
    {
        // Serialized because the shim is process-wide: two sessions initializing at once hand out
        // interfaces that interfere, and the loser then receives no callbacks.
        lock (s_desktopLock)
        {
            var metaHost = Extensions.CLRCreateInstance().CLRMetaHost;
            // Launches always target the current desktop CLR; for attach, ask the live process
            // which CLR it actually loaded instead of assuming (x86/older 4.x installs report
            // the same "v4.0.30319" family, but this catches the exceptions).
            var runtime = Extensions.GetRuntime(metaHost, RuntimeVersion(metaHost, _attachPid));
            var raw = Extensions.GetInterface<ICorDebug>(runtime, Extensions.CLSID_CLRDebuggingLegacy);
            InitializeCorDebug(new CorDebug(raw), callback);
        }
    }

    private void InitializeCorDebug(CorDebug corDebug, CorDebugManagedCallback callback)
    {
        _corDebug = corDebug;
        _corDebug.Initialize();
        _corDebug.SetManagedHandler(callback);
    }

    private void AttachCore(
        string? exe, string[]? args, int attachPid,
        CorDebugManagedCallback callback,
        IReadOnlyDictionary<string, string>? env = null, string? workingDirectory = null)
    {
        if (_runtime == DebugRuntime.CoreClr)
        {
            AttachCoreClr(exe, args, attachPid, callback, env, workingDirectory);
            return;
        }

        if (attachPid != 0)
        {
            EnsureAttachArchitecture(attachPid);
            Pid = attachPid;
            _process = _corDebug!.DebugActiveProcess(attachPid, false);

            // Binding is left to the LoadModule callbacks: attaching makes the runtime replay a
            // synthetic load for every module already in the target, and those callbacks bind the
            // pending breakpoints. Stopping the process to bind by hand here instead races that
            // attach sequence, and the breakpoint then binds but never fires.
            Emit(DebugEventKind.Created, $"attached to pid {attachPid}", string.Empty, 0);
            return;
        }

        var commandLine = args!.Length > 0 ? $"\"{exe}\" {string.Join(' ', args)}" : $"\"{exe}\"";
        var workingDir = string.IsNullOrEmpty(workingDirectory)
            ? Path.GetDirectoryName(exe!) ?? Environment.CurrentDirectory
            : workingDirectory;
        // Launch the debuggee SUSPENDED in a pseudoconsole, attach the managed debugger, then
        // resume. This streams the debuggee's console (full VT) into a terminal while keeping
        // ICorDebug breakpoints/EnC working — the launch-then-attach pattern validated in
        // docs/research/probes/ConPtyDebugProbe. (x64 debuggees; managed-only attach.)
        _child = StartSuspended(commandLine, workingDir, env);
        Pid = _child.ProcessId;
        _process = _corDebug!.DebugActiveProcess(Pid, false);
        _child.ResumeMainThread();
        Emit(DebugEventKind.Created, $"launched {Path.GetFileName(exe)}", string.Empty, 0);
    }

    private void AttachCoreClr(
        string? exe,
        string[]? args,
        int attachPid,
        CorDebugManagedCallback callback,
        IReadOnlyDictionary<string, string>? env = null,
        string? workingDirectory = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("CoreCLR debugging is currently implemented for Windows hosts only");

        if (attachPid != 0)
        {
            AttachRunningCoreClrProcess(attachPid, callback);
            return;
        }

        var commandLine = args!.Length > 0 ? $"\"{exe}\" {string.Join(' ', args)}" : $"\"{exe}\"";
        var workingDir = string.IsNullOrEmpty(workingDirectory)
            ? Path.GetDirectoryName(exe!) ?? Environment.CurrentDirectory
            : workingDirectory;

        _child = StartSuspended(commandLine, workingDir, env);
        Pid = _child.ProcessId;
        var dbgShim = LoadDbgShim();

        var startupReady = new ManualResetEventSlim();
        var attached = new ManualResetEventSlim();
        var callbackReturned = new ManualResetEventSlim();
        CorDebug? startupCorDebug = null;
        HRESULT startupHr = HRESULT.S_OK;
        Exception? callbackError = null;

        _coreClrStartupCallback = (corDebug, _, hr) =>
        {
            try
            {
                startupCorDebug = corDebug;
                startupHr = hr;
                startupReady.Set();
                attached.Wait(TimeSpan.FromSeconds(30));
            }
            catch (Exception ex)
            {
                callbackError = ex;
                startupReady.Set();
            }
            finally
            {
                callbackReturned.Set();
            }
        };

        try
        {
            _coreClrStartupCookie = Extensions.RegisterForRuntimeStartup(
                dbgShim,
                Pid,
                _coreClrStartupCallback,
                IntPtr.Zero);
            _child.ResumeMainThread();

            if (!startupReady.Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("CoreCLR runtime did not start within 30 seconds");
            if (callbackError is not null)
                throw callbackError;
            if (startupHr != HRESULT.S_OK)
                throw new InvalidOperationException($"CoreCLR runtime startup failed: {startupHr}");
            if (startupCorDebug is null)
                throw new InvalidOperationException("CoreCLR runtime startup did not provide ICorDebug");

            InitializeCorDebug(startupCorDebug, callback);
            _process = _corDebug!.DebugActiveProcess(Pid, false);
            Emit(DebugEventKind.Created, $"launched {Path.GetFileName(exe)}", string.Empty, 0);
        }
        catch
        {
            try { _child?.Dispose(); } catch { }
            throw;
        }
        finally
        {
            attached.Set();
            callbackReturned.Wait(TimeSpan.FromSeconds(5));
            if (_coreClrStartupCookie != IntPtr.Zero)
            {
                try { dbgShim.UnregisterForRuntimeStartup(_coreClrStartupCookie); } catch { }
                _coreClrStartupCookie = IntPtr.Zero;
            }
        }
    }

    private void AttachRunningCoreClrProcess(int pid, CorDebugManagedCallback callback)
    {
        EnsureAttachArchitecture(pid);
        Pid = pid;
        var dbgShim = LoadDbgShim();
        try
        {
            var clrs = dbgShim.EnumerateCLRs(pid);
            try
            {
                foreach (var item in clrs.Items)
                {
                    if (string.IsNullOrEmpty(item.Path))
                        continue;
                    var version = dbgShim.CreateVersionStringFromModule(pid, item.Path);
                    var corDebug = dbgShim.CreateDebuggingInterfaceFromVersionEx(
                        CorDebugInterfaceVersion.CorDebugVersion_4_0,
                        version);
                    InitializeCorDebug(corDebug, callback);
                    _process = _corDebug!.DebugActiveProcess(pid, false);
                    Emit(DebugEventKind.Created, $"attached to pid {pid}", string.Empty, 0);
                    return;
                }
            }
            finally
            {
                try { dbgShim.CloseCLREnumeration(clrs); } catch { }
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"CoreCLR attach failed for pid {pid}: {ex.Message}", ex);
        }

        throw new InvalidOperationException($"pid {pid} has no loaded CoreCLR runtime");
    }

    private DbgShim LoadDbgShim()
    {
        if (_dbgShim is not null)
            return _dbgShim;

        var path = FindDbgShimPath()
            ?? throw new FileNotFoundException(
                "dbgshim.dll was not found. Build the Windows host so Microsoft.Diagnostics.DbgShim is copied beside CedHost.");
        _dbgShimModule = NativeLibrary.Load(path);
        _dbgShim = new DbgShim(_dbgShimModule);
        return _dbgShim;
    }

    private static string? FindDbgShimPath()
    {
        var rid = Environment.Is64BitProcess ? "win-x64" : "win-x86";
        foreach (var path in DbgShimCandidates(rid))
        {
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    private static IEnumerable<string> DbgShimCandidates(string rid)
    {
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "dbgshim.dll");
        yield return Path.Combine(baseDir, "dbgshim", rid, "dbgshim.dll");
        yield return Path.Combine(baseDir, "runtimes", rid, "native", "dbgshim.dll");

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (userProfile.Length > 0)
        {
            yield return Path.Combine(
                userProfile,
                ".nuget",
                "packages",
                $"microsoft.diagnostics.dbgshim.{rid}",
                "9.0.661903",
                "runtimes",
                rid,
                "native",
                "dbgshim.dll");
        }
    }

    // --- EnC / module lifecycle -----------------------------------------------------------------

    /// True for the user's own assemblies (EnC/diagnostic targets): everything except framework/
    /// GAC modules — but WebForms shadow copies under "Temporary ASP.NET Files" (which live
    /// below the Framework directory) DO count.
    internal static bool IsUserModule(string moduleName)
    {
        var lower = moduleName.ToLowerInvariant();
        if (lower.Contains("temporary asp.net files"))
            return true;
        return !lower.Contains(@"\microsoft.net\framework")
            && !lower.Contains(@"\windows\assembly\")
            && !lower.Contains(@"\gac_");
    }

    /// JIT-flag a freshly loaded user module for EnC (only valid during the LoadModule callback)
    /// and remember it by simple assembly name as an ApplyHotReload target.
    private void RegisterEncModule(CorDebugModule module, string moduleName)
    {
        if (moduleName.Length == 0 || !IsUserModule(moduleName))
            return;
        // Whether this succeeded decides whether a later delta can be applied at all: a module
        // JITted without the flag is not updatable, and ApplyChanges faults on it rather than
        // failing. Swallowing the result is how that stays invisible until the crash, so it is
        // recorded and only a flagged module is offered as a target.
        var flagged = HRESULT.E_FAIL;
        try { flagged = module.TrySetJITCompilerFlags(CorDebugJITCompilerFlags.CORDEBUG_JIT_ENABLE_ENC); }
        catch (Exception ex)
        {
            Emit(DebugEventKind.Output,
                $"EnC could not be enabled for {Path.GetFileName(moduleName)}: {ex.Message}",
                string.Empty, 0);
        }

        if (flagged != HRESULT.S_OK)
        {
            Emit(DebugEventKind.Output,
                $"EnC is unavailable for {Path.GetFileName(moduleName)} ({flagged}); " +
                "hot reload cannot change it.",
                string.Empty, 0);
            return;
        }

        var assemblyName = Path.GetFileNameWithoutExtension(moduleName);
        if (assemblyName.Length > 0)
            _encModules[assemblyName] = module;
    }

    /// One-time "no symbols" diagnostic per user module, only when source breakpoints exist —
    /// the actionable cause of "my breakpoint never binds".
    private void ReportMissingSymbols(CorDebugModule module, string moduleName)
    {
        if (moduleName.Length == 0 || !IsUserModule(moduleName))
            return;
        bool hasSourceSpecs;
        lock (_specLock)
            hasSourceSpecs = _specs.Any(s => s.FilePath.Length > 0);
        if (!hasSourceSpecs || ReaderFor(module, moduleName) is not null)
            return;
        lock (_noSymbolsReported)
        {
            if (!_noSymbolsReported.Add(moduleName))
                return;
        }
        Emit(
            DebugEventKind.Output,
            $"no symbols for {Path.GetFileName(moduleName)} — source breakpoints in it cannot bind",
            string.Empty, 0);
    }

    /// Module unload (app-domain recycle, plugin unload): its bound breakpoints return to
    /// pending — the specs survive, so the next LoadModule rebinds them — and the client's
    /// gutter dots go hollow again via BreakpointUnbound events.
    private void OnModuleUnloaded(string moduleName)
    {
        var assemblyName = Path.GetFileNameWithoutExtension(moduleName);
        if (assemblyName.Length > 0)
            _encModules.TryRemove(assemblyName, out _);
        foreach (var pair in _boundModule)
        {
            if (!string.Equals(pair.Value, moduleName, StringComparison.OrdinalIgnoreCase))
                continue;
            _boundModule.TryRemove(pair.Key, out _);
            _bound.TryRemove(pair.Key, out _);
            _boundSpecs.TryRemove(pair.Key, out _);
            // Source keys are "path|line"; entry keys have no separator (no gutter echo needed).
            var sep = pair.Key.LastIndexOf('|');
            if (sep > 0 && int.TryParse(pair.Key.AsSpan(sep + 1), out var line))
                Emit(
                    DebugEventKind.BreakpointUnbound,
                    $"module unloaded: {Path.GetFileName(moduleName)}",
                    string.Empty, 0, pair.Key[..sep], line);
        }
    }

    /// Apply one EnC metadata+IL delta to a live module (by simple assembly name), marshalled
    /// onto the session thread. Only legal from a real break state — see below.
    public Task<(bool Ok, string Error)> ApplyDeltaAsync(
        string assemblyName, byte[] metadata, byte[] il, byte[] pdb)
        => InvokeAsync<(bool Ok, string Error)>(() =>
    {
        if (_encPoisoned)
            return (false, "a previous edit failed to apply; this session can no longer be edited");
        if (!_encModules.TryGetValue(assemblyName, out var module))
            return (false, $"module '{assemblyName}' is not loaded in the debuggee");
        var process = _process;
        if (process is null)
            return (false, "no process");

        // Pause-apply-resume does not work here, and not for want of trying. An async break
        // (ICorDebugProcess::Stop) synchronizes the process, and adopting a thread with a live
        // frame gives it a managed stop context too — both were measured, and ApplyChanges
        // access-violates either way, with no HRESULT and no managed exception. The desktop CLR
        // wants a stop that arrived through a debug event, which is why Visual Studio and Rider
        // only offer Apply Code Changes from break mode rather than breaking on your behalf.
        if (_stoppedThread is null)
        {
            return (false,
                "the target is running; .NET Framework applies edits only while stopped at a " +
                "breakpoint, so break first and apply from there");
        }

        try
        {
            var metaPtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(metadata.Length);
            var ilPtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(il.Length);
            try
            {
                System.Runtime.InteropServices.Marshal.Copy(metadata, 0, metaPtr, metadata.Length);
                System.Runtime.InteropServices.Marshal.Copy(il, 0, ilPtr, il.Length);
                var hr = module.TryApplyChanges(metadata.Length, metaPtr, il.Length, ilPtr);
                if (hr != HRESULT.S_OK)
                {
                    // A half-applied edit leaves the runtime's metadata and the debugger's view
                    // disagreeing, and there is no way to roll it back. Further edits would build
                    // on that, so the session stops accepting them.
                    _encPoisoned = true;
                    return (false, $"ApplyChanges failed: {hr}");
                }
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(metaPtr);
                System.Runtime.InteropServices.Marshal.FreeHGlobal(ilPtr);
            }

            RefreshSymbolsAfterEdit(assemblyName, pdb);
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    });

    /// <summary>
    /// Brings the debugger's own view back in line with the edit the runtime just took.
    /// </summary>
    /// <remarks>
    /// <c>ApplyChanges</c> updates the runtime and nothing else. Line numbers, sequence points and
    /// local scopes for the edited method live in the debugger's symbol reader, which still holds
    /// the pre-edit PDB — so without this every breakpoint and every reported location in that
    /// method silently points at the old source. The unmanaged reader takes the delta directly
    /// (this is what MDbg's ApplyEdit does); a portable reader has no equivalent, so its cache is
    /// dropped instead and the stale entry is at least not kept.
    /// </remarks>
    private void RefreshSymbolsAfterEdit(string assemblyName, byte[] pdb)
    {
        foreach (var (path, reader) in _readers.ToArray())
        {
            if (!string.Equals(
                    Path.GetFileNameWithoutExtension(path), assemblyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool updated = false;
            if (reader?.Unmanaged is { } unmanaged && pdb.Length > 0)
            {
                // By file rather than by IStream: the reader accepts a path, and it keeps the
                // COM plumbing out of a path that already has enough ways to fail.
                string temporary = Path.Combine(
                    Path.GetTempPath(), $"roslyn-sense-enc-{Guid.NewGuid():N}.pdb");
                try
                {
                    File.WriteAllBytes(temporary, pdb);
                    updated = unmanaged.TryUpdateSymbolStore(temporary, null) == HRESULT.S_OK;
                }
                catch (Exception ex)
                {
                    Emit(DebugEventKind.Output,
                        $"the symbol store could not be updated after the edit: {ex.Message}",
                        string.Empty, 0);
                }
                finally
                {
                    try { File.Delete(temporary); } catch { }
                }
            }

            if (!updated)
            {
                _readers.Remove(path);
                reader?.Dispose();
                Emit(DebugEventKind.Output,
                    $"line information for {assemblyName} is stale after the edit; " +
                    "breakpoints in changed methods may bind to the wrong line.",
                    string.Empty, 0);
            }
        }

        // A method token alone no longer identifies code: the edited method has a new version, and
        // bindings made against the old one would resolve to it. Dropping them returns the
        // affected breakpoints to pending, and the specs survive, so they rebind.
        foreach (var pair in _boundModule.ToArray())
        {
            if (!string.Equals(
                    Path.GetFileNameWithoutExtension(pair.Value), assemblyName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _boundModule.TryRemove(pair.Key, out _);
            _bound.TryRemove(pair.Key, out _);
            _boundSpecs.TryRemove(pair.Key, out _);
        }
    }

    // --- breakpoints ----------------------------------------------------------------------------

    private BreakpointSpec[] SpecsSnapshot()
    {
        lock (_specLock)
            return _specs.ToArray();
    }

    private static string SourceKey(string filePath, int line)
        => $"{Path.GetFullPath(filePath)}|{line}".ToLowerInvariant();

    private static SourceRange SourceRangeOf(
        string filePath,
        int line,
        int column = 0,
        int endLine = 0,
        int endColumn = 0)
        => new()
        {
            FilePath = filePath,
            Line = (uint)Math.Max(0, line),
            Column = (uint)Math.Max(0, column),
            EndLine = (uint)Math.Max(0, endLine == 0 ? line : endLine),
            EndColumn = (uint)Math.Max(0, endColumn),
        };

    private readonly record struct SequencePointMatch(
        int Offset,
        int Line,
        int Column,
        int EndLine,
        int EndColumn,
        string FilePath);

    private readonly record struct ResolvedSequencePoint(mdMethodDef MethodToken, SequencePointMatch Match);

    private readonly record struct SymbolDocument(string FilePath, SymUnmanagedDocument? Unmanaged, DocumentHandle Portable);

    private sealed class PortablePdbReader : IDisposable
    {
        private readonly FileStream _stream;
        private readonly MetadataReaderProvider _provider;

        private PortablePdbReader(FileStream stream, MetadataReaderProvider provider)
        {
            _stream = stream;
            _provider = provider;
            Reader = provider.GetMetadataReader();
        }

        public MetadataReader Reader { get; }

        public static PortablePdbReader? Open(string modulePath)
        {
            var pdbPath = Path.ChangeExtension(modulePath, ".pdb");
            if (!File.Exists(pdbPath))
                return null;
            var stream = File.OpenRead(pdbPath);
            try
            {
                var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
                return new PortablePdbReader(stream, provider);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _provider.Dispose();
            _stream.Dispose();
        }
    }

    private sealed class SymbolReader : IDisposable
    {
        private SymbolReader(SymUnmanagedReader? unmanaged, PortablePdbReader? portable)
        {
            Unmanaged = unmanaged;
            Portable = portable;
        }

        public SymUnmanagedReader? Unmanaged { get; }
        public PortablePdbReader? Portable { get; }

        public static SymbolReader? Open(string modulePath, MetaDataImport metadata)
        {
            try
            {
                return new SymbolReader(CreateUnmanagedSymbolReader(modulePath, metadata), null);
            }
            catch
            {
            }

            try
            {
                var portable = PortablePdbReader.Open(modulePath);
                return portable is null ? null : new SymbolReader(null, portable);
            }
            catch
            {
                return null;
            }
        }

        public void Dispose() => Portable?.Dispose();
    }

    private static bool IsHiddenSequencePoint(int line) => line == 0xFEEFEE;

    private static SequencePointMatch? BestSequencePoint(
        GetSequencePointsResult points,
        SymUnmanagedDocument document,
        int requestedLine,
        int requestedColumn)
    {
        SequencePointMatch? fallback = null;
        var file = Safe(() => document.URL) ?? string.Empty;
        for (var i = 0; i < points.offsets.Length; i++)
        {
            var line = points.lines[i];
            if (IsHiddenSequencePoint(line))
                continue;
            var column = i < points.columns.Length ? points.columns[i] : 0;
            var endLine = i < points.endLines.Length ? points.endLines[i] : line;
            var endColumn = i < points.endColumns.Length ? points.endColumns[i] : 0;
            var candidate = new SequencePointMatch(
                points.offsets[i],
                line,
                column,
                endLine == 0 ? line : endLine,
                endColumn,
                file);
            if (line == requestedLine)
            {
                if (requestedColumn <= 1 || column == 0 || column >= requestedColumn)
                    return candidate;
                fallback ??= candidate;
            }
            else if (line > requestedLine && fallback is null)
            {
                fallback = candidate;
            }
        }
        return fallback;
    }

    private static ResolvedSequencePoint? BestSequencePointInDocument(
        SymbolReader reader,
        SymbolDocument document,
        int requestedLine,
        int requestedColumn)
    {
        if (reader.Unmanaged is not null && document.Unmanaged is not null)
            return BestUnmanagedSequencePointInDocument(
                reader.Unmanaged,
                document.Unmanaged,
                requestedLine,
                requestedColumn);
        if (reader.Portable is not null && !document.Portable.IsNil)
            return BestPortableSequencePointInDocument(
                reader.Portable.Reader,
                document,
                requestedLine,
                requestedColumn);
        return null;
    }

    private static ResolvedSequencePoint? BestUnmanagedSequencePointInDocument(
        SymUnmanagedReader reader,
        SymUnmanagedDocument document,
        int requestedLine,
        int requestedColumn)
    {
        var candidates = new List<ResolvedSequencePoint>();

        void AddCandidate(SymUnmanagedMethod method)
        {
            try
            {
                var points = method.GetSequencePoints(method.SequencePointCount);
                if (BestSequencePoint(points, document, requestedLine, requestedColumn) is { } match)
                    candidates.Add(new ResolvedSequencePoint(method.Token, match));
            }
            catch
            {
            }
        }

        try
        {
            AddCandidate(reader.GetMethodFromDocumentPosition(
                document.Raw,
                requestedLine,
                Math.Max(1, requestedColumn)));
        }
        catch
        {
        }

        foreach (var method in Safe(() => reader.GetMethodsInDocument(document.Raw)) ?? Array.Empty<SymUnmanagedMethod>())
            AddCandidate(method);

        return BestCandidate(candidates, requestedLine);
    }

    private static ResolvedSequencePoint? BestPortableSequencePointInDocument(
        MetadataReader reader,
        SymbolDocument document,
        int requestedLine,
        int requestedColumn)
    {
        var candidates = new List<ResolvedSequencePoint>();
        foreach (var handle in reader.MethodDebugInformation)
        {
            var method = reader.GetMethodDebugInformation(handle);
            AddPortableCandidate(reader, handle, method, document, requestedLine, requestedColumn, candidates);
        }
        return BestCandidate(candidates, requestedLine);
    }

    private static void AddPortableCandidate(
        MetadataReader reader,
        MethodDebugInformationHandle handle,
        MethodDebugInformation method,
        SymbolDocument document,
        int requestedLine,
        int requestedColumn,
        List<ResolvedSequencePoint> candidates)
    {
        SequencePointMatch? fallback = null;
        var token = PortableMethodToken(handle);
        foreach (var point in method.GetSequencePoints())
        {
            if (point.IsHidden || !PortableDocumentMatches(reader, method, point, document))
                continue;
            var candidate = new SequencePointMatch(
                point.Offset,
                point.StartLine,
                point.StartColumn,
                point.EndLine == 0 ? point.StartLine : point.EndLine,
                point.EndColumn,
                PortableSequencePointFile(reader, method, point));
            if (candidate.Line == requestedLine)
            {
                if (requestedColumn <= 1 || candidate.Column == 0 || candidate.Column >= requestedColumn)
                {
                    candidates.Add(new ResolvedSequencePoint(token, candidate));
                    return;
                }
                fallback ??= candidate;
            }
            else if (candidate.Line > requestedLine && fallback is null)
            {
                fallback = candidate;
            }
        }
        if (fallback is { } match)
            candidates.Add(new ResolvedSequencePoint(token, match));
    }

    private static ResolvedSequencePoint? BestCandidate(List<ResolvedSequencePoint> candidates, int requestedLine)
    {
        if (candidates.Count == 0)
            return null;
        return candidates
            .OrderBy(candidate => candidate.Match.Line < requestedLine ? 1 : 0)
            .ThenBy(candidate => candidate.Match.Line)
            .ThenBy(candidate => candidate.Match.Column)
            .First();
    }

    private static mdMethodDef PortableMethodToken(MethodDebugInformationHandle handle)
        => (mdMethodDef)MetadataTokens.GetToken(MetadataTokens.MethodDefinitionHandle(MetadataTokens.GetRowNumber(handle)));

    private static MethodDefinitionHandle PortableMethodHandle(mdMethodDef token)
        => MetadataTokens.MethodDefinitionHandle((int)token & 0x00FFFFFF);

    private static bool PortableDocumentMatches(
        MetadataReader reader,
        MethodDebugInformation method,
        SequencePoint point,
        SymbolDocument document)
    {
        var handle = point.Document.IsNil ? method.Document : point.Document;
        return !handle.IsNil && handle.Equals(document.Portable);
    }

    private static string PortableSequencePointFile(
        MetadataReader reader,
        MethodDebugInformation method,
        SequencePoint point)
    {
        var handle = point.Document.IsNil ? method.Document : point.Document;
        if (handle.IsNil)
            return string.Empty;
        return Safe(() => reader.GetString(reader.GetDocument(handle).Name)) ?? string.Empty;
    }

    private static bool SequencePointOverlapsLine(GetSequencePointsResult points, int index, int line)
    {
        if (IsHiddenSequencePoint(points.lines[index]))
            return false;
        var start = points.lines[index];
        var end = index < points.endLines.Length && points.endLines[index] != 0
            ? points.endLines[index]
            : start;
        return start <= line && line <= end;
    }

    private BreakpointLocation? ResolveBestLocation(string filePath, int line, int column)
    {
        foreach (var module in LoadedModules())
        {
            var location = ResolveBestLocationInModule(module, filePath, line, column);
            if (location is not null)
                return location;
        }
        return null;
    }

    private BreakpointLocation? ResolveBestLocationInModule(
        CorDebugModule module,
        string filePath,
        int line,
        int column)
    {
        try
        {
            var moduleName = Safe(() => module.Name) ?? string.Empty;
            if (moduleName.Length == 0)
                return null;
            var reader = ReaderFor(module, moduleName);
            if (reader is null)
                return null;
            var document = FindDocument(reader, filePath);
            if (document is null)
                return null;
            var resolved = BestSequencePointInDocument(reader, document.Value, line, column);
            if (resolved is null)
                return null;
            var match = resolved.Value.Match;
            var actual = SourceRangeOf(filePath, match.Line, match.Column, match.EndLine, match.EndColumn);
            var requested = SourceRangeOf(filePath, line, column);
            return new BreakpointLocation
            {
                Id = $"{Path.GetFullPath(filePath).ToLowerInvariant()}:{actual.Line}:{actual.Column}",
                Requested = requested,
                Actual = actual,
                Verified = true,
                Label = actual.Column > 0 ? $"column {actual.Column}" : $"line {actual.Line}",
                Kind = BreakpointKind.Source,
            };
        }
        catch
        {
            return null;
        }
    }

    private (BreakpointLocation Location, int Offset)? ResolveBestLocationInFrame(
        CorDebugILFrame frame,
        string filePath,
        int line,
        int column)
    {
        try
        {
            var function = frame.Function;
            var reader = ReaderFor(function.Module, function.Module.Name);
            if (reader is null)
                return null;
            var document = FindDocument(reader, filePath);
            if (document is null)
                return null;
            var match = BestSequencePointInMethod(reader, function.Token, document.Value, line, column);
            if (match is null)
                return null;
            var actual = SourceRangeOf(filePath, match.Value.Line, match.Value.Column, match.Value.EndLine, match.Value.EndColumn);
            var requested = SourceRangeOf(filePath, line, column);
            var location = new BreakpointLocation
            {
                Requested = requested,
                Actual = actual,
                Verified = true,
                Label = match.Value.Column > 0 ? $"column {match.Value.Column}" : $"line {match.Value.Line}",
                Kind = BreakpointKind.Source,
            };
            return (location, match.Value.Offset);
        }
        catch
        {
            return null;
        }
    }

    private static SequencePointMatch? BestSequencePointInMethod(
        SymbolReader reader,
        mdMethodDef methodToken,
        SymbolDocument document,
        int line,
        int column)
    {
        if (reader.Unmanaged is not null && document.Unmanaged is not null)
        {
            try
            {
                var method = reader.Unmanaged.GetMethod(methodToken);
                return BestSequencePoint(method.GetSequencePoints(method.SequencePointCount), document.Unmanaged, line, column);
            }
            catch
            {
                return null;
            }
        }

        if (reader.Portable is null || document.Portable.IsNil)
            return null;
        try
        {
            var method = reader.Portable.Reader.GetMethodDebugInformation(PortableMethodHandle(methodToken));
            SequencePointMatch? fallback = null;
            foreach (var point in method.GetSequencePoints())
            {
                if (point.IsHidden || !PortableDocumentMatches(reader.Portable.Reader, method, point, document))
                    continue;
                var candidate = new SequencePointMatch(
                    point.Offset,
                    point.StartLine,
                    point.StartColumn,
                    point.EndLine == 0 ? point.StartLine : point.EndLine,
                    point.EndColumn,
                    PortableSequencePointFile(reader.Portable.Reader, method, point));
                if (candidate.Line == line)
                {
                    if (column <= 1 || candidate.Column == 0 || candidate.Column >= column)
                        return candidate;
                    fallback ??= candidate;
                }
                else if (candidate.Line > line && fallback is null)
                {
                    fallback = candidate;
                }
            }
            return fallback;
        }
        catch
        {
            return null;
        }
    }

    private static SequencePointMatch? SequencePointAtOffset(
        SymbolReader reader,
        mdMethodDef methodToken,
        int ip)
    {
        if (reader.Unmanaged is not null)
        {
            try
            {
                var method = reader.Unmanaged.GetMethod(methodToken);
                var points = method.GetSequencePoints(method.SequencePointCount);
                for (var i = 0; i < points.offsets.Length; i++)
                {
                    var start = points.offsets[i];
                    var end = i + 1 < points.offsets.Length ? points.offsets[i + 1] : int.MaxValue;
                    if (ip < start || ip >= end || IsHiddenSequencePoint(points.lines[i]))
                        continue;
                    var document = new SymUnmanagedDocument(points.documents[i]);
                    var file = Safe(() => document.URL) ?? string.Empty;
                    var column = i < points.columns.Length ? points.columns[i] : 0;
                    var endLine = i < points.endLines.Length && points.endLines[i] != 0 ? points.endLines[i] : points.lines[i];
                    var endColumn = i < points.endColumns.Length ? points.endColumns[i] : 0;
                    return new SequencePointMatch(start, points.lines[i], column, endLine, endColumn, file);
                }
            }
            catch
            {
                return null;
            }
        }

        if (reader.Portable is null)
            return null;
        try
        {
            var method = reader.Portable.Reader.GetMethodDebugInformation(PortableMethodHandle(methodToken));
            var materialized = method.GetSequencePoints().Where(p => !p.IsHidden).ToArray();
            for (var i = 0; i < materialized.Length; i++)
            {
                var point = materialized[i];
                var start = point.Offset;
                var end = i + 1 < materialized.Length ? materialized[i + 1].Offset : int.MaxValue;
                if (ip < start || ip >= end)
                    continue;
                return new SequencePointMatch(
                    start,
                    point.StartLine,
                    point.StartColumn,
                    point.EndLine == 0 ? point.StartLine : point.EndLine,
                    point.EndColumn,
                    PortableSequencePointFile(reader.Portable.Reader, method, point));
            }
        }
        catch
        {
            return null;
        }
        return null;
    }

    private IEnumerable<BreakpointLocation> BreakpointLocationsInModule(
        CorDebugModule module,
        BreakpointLocationsRequest request)
    {
        var moduleName = Safe(() => module.Name) ?? string.Empty;
        if (moduleName.Length == 0)
            yield break;
        var reader = ReaderFor(module, moduleName);
        if (reader is null)
            yield break;
        var document = FindDocument(reader, request.FilePath);
        if (document is null)
            yield break;

        if (reader.Unmanaged is not null && document.Value.Unmanaged is not null)
        {
            var requestedLine = (int)request.Line;
            var requestedColumn = (int)request.Column;
            SymUnmanagedMethod? primary = null;
            try
            {
                primary = reader.Unmanaged.GetMethodFromDocumentPosition(
                    document.Value.Unmanaged.Raw,
                    requestedLine,
                    Math.Max(1, requestedColumn));
            }
            catch
            {
            }

            var emitted = false;
            if (primary is not null)
            {
                foreach (var location in LocationsForUnmanagedMethod(primary, document.Value.Unmanaged, request))
                {
                    emitted = true;
                    yield return location;
                }
            }
            if (emitted)
                yield break;

            foreach (var method in Safe(() => reader.Unmanaged.GetMethodsInDocument(document.Value.Unmanaged.Raw))
                ?? Array.Empty<SymUnmanagedMethod>())
            {
                foreach (var location in LocationsForUnmanagedMethod(method, document.Value.Unmanaged, request))
                    yield return location;
            }
            yield break;
        }

        if (reader.Portable is null)
            yield break;

        foreach (var handle in reader.Portable.Reader.MethodDebugInformation)
            foreach (var location in LocationsForPortableMethod(
                reader.Portable.Reader,
                handle,
                reader.Portable.Reader.GetMethodDebugInformation(handle),
                document.Value,
                request))
                yield return location;
    }

    private static IEnumerable<BreakpointLocation> LocationsForUnmanagedMethod(
        SymUnmanagedMethod method,
        SymUnmanagedDocument document,
        BreakpointLocationsRequest request)
    {
        GetSequencePointsResult points;
        try
        {
            points = method.GetSequencePoints(method.SequencePointCount);
        }
        catch
        {
            yield break;
        }
        var file = Safe(() => document.URL) ?? request.FilePath;
        var requested = SourceRangeOf(request.FilePath, (int)request.Line, (int)request.Column);
        for (var i = 0; i < points.offsets.Length; i++)
        {
            var pointDocument = new SymUnmanagedDocument(points.documents[i]);
            var pointFile = Safe(() => pointDocument.URL) ?? string.Empty;
            if (pointFile.Length == 0
                || !string.Equals(Path.GetFullPath(pointFile), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase)
                || !SequencePointOverlapsLine(points, i, (int)request.Line))
                continue;
            var line = points.lines[i];
            var column = i < points.columns.Length ? points.columns[i] : 0;
            var endLine = i < points.endLines.Length && points.endLines[i] != 0 ? points.endLines[i] : line;
            var endColumn = i < points.endColumns.Length ? points.endColumns[i] : 0;
            var actual = SourceRangeOf(file, line, column, endLine, endColumn);
            yield return new BreakpointLocation
            {
                Id = $"{Path.GetFullPath(file).ToLowerInvariant()}:{line}:{column}",
                Requested = requested,
                Actual = actual,
                Verified = true,
                Message = string.Empty,
                Label = column > 0 ? $"column {column}" : $"line {line}",
                Kind = BreakpointKind.Source,
            };
        }
    }

    private static IEnumerable<BreakpointLocation> LocationsForPortableMethod(
        MetadataReader reader,
        MethodDebugInformationHandle handle,
        MethodDebugInformation method,
        SymbolDocument document,
        BreakpointLocationsRequest request)
    {
        var requested = SourceRangeOf(request.FilePath, (int)request.Line, (int)request.Column);
        foreach (var point in method.GetSequencePoints())
        {
            if (point.IsHidden
                || !PortableDocumentMatches(reader, method, point, document)
                || point.StartLine > (int)request.Line
                || ((point.EndLine == 0 ? point.StartLine : point.EndLine) < (int)request.Line))
                continue;

            var file = PortableSequencePointFile(reader, method, point);
            if (file.Length == 0)
                file = document.FilePath;
            var actual = SourceRangeOf(file, point.StartLine, point.StartColumn, point.EndLine, point.EndColumn);
            yield return new BreakpointLocation
            {
                Id = $"{Path.GetFullPath(request.FilePath).ToLowerInvariant()}:{point.StartLine}:{point.StartColumn}",
                Requested = requested,
                Actual = actual,
                Verified = true,
                Message = string.Empty,
                Label = point.StartColumn > 0 ? $"column {point.StartColumn}" : $"line {point.StartLine}",
                Kind = BreakpointKind.Source,
            };
        }
    }

    private static void DeduplicateLocations(IList<BreakpointLocation> locations)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = locations.Count - 1; i >= 0; i--)
        {
            var actual = locations[i].Actual;
            var key = actual is null
                ? locations[i].Id
                : $"{actual.FilePath}|{actual.Line}|{actual.Column}";
            if (!seen.Add(key))
                locations.RemoveAt(i);
        }
        var sorted = locations
            .OrderBy(l => l.Actual?.Line ?? 0)
            .ThenBy(l => l.Actual?.Column ?? 0)
            .ToArray();
        locations.Clear();
        foreach (var location in sorted)
            locations.Add(location);
    }

    private bool TryGetBoundSpec(string filePath, int line, out BreakpointSpec spec)
        => _boundSpecs.TryGetValue(SourceKey(filePath, line), out spec!);

    private IEnumerable<CorDebugModule> LoadedModules()
    {
        var process = _process;
        if (process is null)
            yield break;
        foreach (var appDomain in Safe(() => process.AppDomains) ?? Array.Empty<CorDebugAppDomain>())
            foreach (var assembly in Safe(() => appDomain.Assemblies) ?? Array.Empty<CorDebugAssembly>())
                foreach (var module in Safe(() => assembly.Modules) ?? Array.Empty<CorDebugModule>())
                    yield return module;
    }

    private void TryBindBreakpoint(CorDebugModule module, BreakpointSpec spec)
    {
        if (spec.FilePath.Length > 0)
            TryBindSourceBreakpoint(module, spec);
        else
            TryBindEntryBreakpoint(module, spec);
    }

    /// Source-line binding via the module's PDB: document lookup → method at line → sequence
    /// point → IL-offset breakpoint. Modules without matching symbols/documents are skipped, so
    /// a pending breakpoint binds when its (possibly shadow-copied) module finally loads.
    private void TryBindSourceBreakpoint(CorDebugModule module, BreakpointSpec spec)
    {
        var requestedLine = (int)spec.Line;
        var requestedColumn = (int)spec.Column;
        var requested = SourceRangeOf(spec.FilePath, requestedLine, requestedColumn);
        var requestedKey = SourceKey(spec.FilePath, requestedLine);
        if (_bound.ContainsKey(requestedKey))
            return;
        try
        {
            var moduleName = Safe(() => module.Name) ?? string.Empty;
            if (moduleName.Length == 0)
                return;
            var reader = ReaderFor(module, moduleName);
            if (reader is null)
                return;

            var document = FindDocument(reader, spec.FilePath);
            if (document is null)
                return;

            var resolved = BestSequencePointInDocument(reader, document.Value, requestedLine, requestedColumn);
            if (resolved is null)
                return;
            var match = resolved.Value.Match;
            var actual = SourceRangeOf(
                spec.FilePath,
                match.Line,
                match.Column,
                match.EndLine,
                match.EndColumn);
            var actualKey = SourceKey(spec.FilePath, match.Line);
            if (_bound.ContainsKey(actualKey))
                return;

            var function = module.GetFunctionFromToken(resolved.Value.MethodToken);
            var breakpoint = function.ILCode.CreateBreakpoint(match.Offset);
            breakpoint.Activate(true);
            spec.Line = (uint)match.Line;
            spec.Column = (uint)match.Column;
            spec.EndLine = (uint)match.EndLine;
            spec.EndColumn = (uint)match.EndColumn;
            spec.Kind = spec.Kind == BreakpointKind.Unspecified ? BreakpointKind.Source : spec.Kind;
            _bound[actualKey] = breakpoint;
            _boundModule[actualKey] = moduleName;
            _boundSpecs[actualKey] = spec;
            if (requestedKey != actualKey)
                _bound.TryRemove(requestedKey, out _);
            Emit(
                DebugEventKind.BreakpointBound,
                requested.Line == actual.Line
                    ? $"bound at line {actual.Line} in {Path.GetFileName(moduleName)}"
                    : $"moved to line {actual.Line} in {Path.GetFileName(moduleName)}",
                string.Empty,
                0,
                actual.FilePath,
                (int)actual.Line,
                (int)actual.Column,
                requestedLocation: requested,
                actualLocation: actual,
                breakpointId: spec.Id);
        }
        catch
        {
            // No symbols / document not in this module — stays pending for later modules.
        }
    }

    private void TryBindEntryBreakpoint(CorDebugModule module, BreakpointSpec spec)
    {
        var moduleName = Safe(() => module.Name) ?? string.Empty;
        var key = $"{moduleName}!{spec.TypeName}.{spec.MethodName}";
        if (!_bound.TryAdd(key, null!))
            return;
        try
        {
            var metadata = Extensions.GetMetaDataInterface<MetaDataImport>(module);
            foreach (var typeDef in Extensions.EnumTypeDefs(metadata))
            {
                var typeProps = metadata.GetTypeDefProps(typeDef);
                if (spec.TypeName.Length > 0 &&
                    !typeProps.szTypeDef.EndsWith(spec.TypeName, StringComparison.Ordinal))
                    continue;
                foreach (var methodDef in Extensions.EnumMethods(metadata, typeDef))
                {
                    var methodProps = metadata.GetMethodProps(methodDef);
                    if (!string.Equals(methodProps.szMethod, spec.MethodName, StringComparison.Ordinal))
                        continue;
                    var function = module.GetFunctionFromToken(methodDef);
                    var breakpoint = function.CreateBreakpoint();
                    breakpoint.Activate(true);
                    _bound[key] = breakpoint;
                    _boundModule[key] = moduleName;
                    Emit(DebugEventKind.Output, $"bound breakpoint {typeProps.szTypeDef}.{methodProps.szMethod}", methodProps.szMethod, 0);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[debug] bind failed: {ex.Message}");
        }
    }

    // --- symbols --------------------------------------------------------------------------------

    private SymbolReader? ReaderFor(CorDebugModule module, string moduleName)
    {
        if (_readers.TryGetValue(moduleName, out var cached))
            return cached;
        SymbolReader? reader = null;
        try
        {
            var metadata = Extensions.GetMetaDataInterface<MetaDataImport>(module);
            reader = SymbolReader.Open(moduleName, metadata);
        }
        catch
        {
            // No PDB for this module.
        }
        _readers[moduleName] = reader;
        return reader;
    }

    /// <summary>
    /// Opens a Windows PDB through diasymreader, the reader Visual Studio itself uses. This is the
    /// path .NET Framework debugging depends on, since those PDBs are not portable-format.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static SymUnmanagedReader CreateUnmanagedSymbolReader(string modulePath, MetaDataImport metadata)
    {
        var clsidCorSymBinderSxs = new Guid("0A29FF9E-7F9C-4437-8B11-F424491E3931");
        var binderType = Type.GetTypeFromCLSID(clsidCorSymBinderSxs, throwOnError: true)!;
        var binderObject = Activator.CreateInstance(binderType)!;
        var binderUnknown = Marshal.GetIUnknownForObject(binderObject);
        ISymUnmanagedBinder rawBinder;
        try
        {
            rawBinder = Extensions.GetObjectForIUnknown<ISymUnmanagedBinder>(binderUnknown);
        }
        finally
        {
            Marshal.Release(binderUnknown);
        }
        var binder = new SymUnmanagedBinder(rawBinder);
        var searchPath = Path.GetDirectoryName(modulePath) ?? Environment.CurrentDirectory;
        return binder.GetReaderForFile(metadata.Raw, modulePath, searchPath);
    }

    private static SymbolDocument? FindDocument(SymbolReader reader, string filePath)
    {
        if (reader.Unmanaged is not null)
        {
            try
            {
                var full = Path.GetFullPath(filePath);
                foreach (var document in reader.Unmanaged.Documents)
                {
                    var url = Safe(() => document.URL);
                    if (url is not null && string.Equals(Path.GetFullPath(url), full, StringComparison.OrdinalIgnoreCase))
                        return new SymbolDocument(url, document, default);
                }
            }
            catch
            {
            }
        }

        if (reader.Portable is not null)
        {
            try
            {
                var full = Path.GetFullPath(filePath);
                foreach (var handle in reader.Portable.Reader.Documents)
                {
                    var document = reader.Portable.Reader.GetDocument(handle);
                    var url = reader.Portable.Reader.GetString(document.Name);
                    if (url.Length > 0 && string.Equals(Path.GetFullPath(url), full, StringComparison.OrdinalIgnoreCase))
                        return new SymbolDocument(url, null, handle);
                }
            }
            catch
            {
            }
        }

        return null;
    }

    // --- stop-state inspection --------------------------------------------------------------

    private static CorDebugFrame? FrameAt(CorDebugThread thread, uint index)
    {
        var i = 0u;
        foreach (var chain in Safe(() => thread.Chains) ?? Array.Empty<CorDebugChain>())
            foreach (var frame in Safe(() => chain.Frames) ?? Array.Empty<CorDebugFrame>())
            {
                if (i == index)
                    return frame;
                i++;
            }
        return null;
    }

    private (string File, int Line, int Column) ThreadLocation(CorDebugThread thread)
    {
        try
        {
            return FrameLocation(thread.ActiveFrame);
        }
        catch
        {
            return (string.Empty, 0, 0);
        }
    }

    /// Map a frame's IP to source through the module PDB's sequence points.
    private (string File, int Line, int Column) FrameLocation(CorDebugFrame frame)
    {
        try
        {
            if (frame is not CorDebugILFrame ilFrame)
                return (string.Empty, 0, 0);
            var function = frame.Function;
            var moduleName = function.Module.Name;
            var reader = ReaderFor(function.Module, moduleName);
            if (reader is null)
                return (string.Empty, 0, 0);
            var match = SequencePointAtOffset(reader, function.Token, ilFrame.IP.pnOffset);
            if (match is not null)
                return (match.Value.FilePath, match.Value.Line, match.Value.Column);
        }
        catch
        {
        }
        return (string.Empty, 0, 0);
    }

    private bool TryGetSourceStepRange(CorDebugILFrame frame, out COR_DEBUG_STEP_RANGE range)
    {
        range = default;
        try
        {
            var function = frame.Function;
            var reader = ReaderFor(function.Module, function.Module.Name);
            if (reader is null)
                return false;
            var match = SequencePointAtOffset(reader, function.Token, frame.IP.pnOffset);
            if (match is not null)
            {
                range = new COR_DEBUG_STEP_RANGE
                {
                    startOffset = match.Value.Offset,
                    endOffset = match.Value.Offset + 1,
                };
                return true;
            }
        }
        catch
        {
        }
        return false;
    }

    private (Dictionary<int, string> Args, Dictionary<int, string> Locals) FrameSymbolNames(CorDebugILFrame frame)
    {
        var args = new Dictionary<int, string>();
        var locals = new Dictionary<int, string>();
        try
        {
            var function = frame.Function;
            var metadata = Extensions.GetMetaDataInterface<MetaDataImport>(function.Module);
            // Metadata parameter names (PDB param enumeration returned E_NOTIMPL on framework PDBs).
            try
            {
                foreach (var parameterToken in Extensions.EnumParams(metadata, function.Token))
                {
                    var parameter = metadata.GetParamProps(parameterToken);
                    if (parameter.pulSequence > 0 && !string.IsNullOrEmpty(parameter.szName))
                        args.TryAdd(parameter.pulSequence - 1, parameter.szName);
                }
            }
            catch
            {
            }

            var reader = ReaderFor(function.Module, function.Module.Name);
            if (reader is null)
                return (args, locals);
            var ip = frame.IP.pnOffset;
            if (reader.Unmanaged is not null)
            {
                var method = reader.Unmanaged.GetMethod(function.Token);
                if (method.TryGetRootScope(out var rootScope) == HRESULT.S_OK && rootScope is not null)
                    AddScopeLocalNames(rootScope, ip, locals);
            }
            else if (reader.Portable is not null)
            {
                AddPortableLocalNames(reader.Portable.Reader, function.Token, ip, locals);
            }
        }
        catch
        {
        }
        return (args, locals);
    }

    private static void AddScopeLocalNames(SymUnmanagedScope scope, int ip, Dictionary<int, string> locals)
    {
        var start = Safe(() => (int?)scope.StartOffset);
        var end = Safe(() => (int?)scope.EndOffset);
        if (start.HasValue && end.HasValue && (ip < start.Value || ip > end.Value))
            return;
        foreach (var local in Safe(() => scope.Locals) ?? Array.Empty<SymUnmanagedVariable>())
        {
            if (local.AddressKind == CorSymAddrKind.ADDR_IL_OFFSET && local.AddressField1 >= 0)
                locals.TryAdd(local.AddressField1, local.Name);
        }
        foreach (var child in Safe(() => scope.Children) ?? Array.Empty<SymUnmanagedScope>())
            AddScopeLocalNames(child, ip, locals);
    }

    private static void AddPortableLocalNames(
        MetadataReader reader,
        mdMethodDef methodToken,
        int ip,
        Dictionary<int, string> locals)
    {
        try
        {
            foreach (var scopeHandle in reader.GetLocalScopes(PortableMethodHandle(methodToken)))
            {
                var scope = reader.GetLocalScope(scopeHandle);
                if (ip < scope.StartOffset || ip > scope.EndOffset)
                    continue;
                foreach (var localHandle in scope.GetLocalVariables())
                {
                    var local = reader.GetLocalVariable(localHandle);
                    var name = reader.GetString(local.Name);
                    if (name.Length > 0)
                        locals.TryAdd(local.Index, name);
                }
            }
        }
        catch
        {
        }
    }

    private static void AppendValues(
        List<DebugVariable> into, string kind, CorDebugValue[]? values, Dictionary<int, string> names)
    {
        if (values is null)
            return;
        for (var i = 0; i < values.Length; i++)
        {
            into.Add(new DebugVariable
            {
                Name = names.TryGetValue(i, out var name) ? name : $"{kind}{i}",
                Value = DescribeValue(values[i]),
                Kind = kind,
            });
        }
    }

    private static string DescribeValue(CorDebugValue value)
    {
        try
        {
            // Chase references so strings/objects describe the target, not the pointer.
            var dereferenced = value;
            if (value is CorDebugReferenceValue reference)
            {
                if (Safe(() => (bool?)reference.IsNull) == true)
                    return "null";
                dereferenced = Safe(() => reference.Dereference()) ?? value;
            }
            if (dereferenced is CorDebugStringValue str)
                return $"\"{Safe(() => str.GetString(str.Length)) ?? string.Empty}\"";
            var scalar = TryReadScalar(dereferenced);
            if (scalar is not null)
                return scalar;
            return Safe(() => dereferenced.Type.ToString()) ?? "?";
        }
        catch
        {
            return "?";
        }
    }

    private static string? TryReadScalar(CorDebugValue value)
    {
        try
        {
            var generic = Extensions.As<CorDebugGenericValue>(value);
            var pointer = Marshal.AllocHGlobal(Math.Max(value.Size, 8));
            try
            {
                generic.GetValue(pointer);
                return value.Type switch
                {
                    CorElementType.Boolean => (Marshal.ReadByte(pointer) != 0).ToString(),
                    CorElementType.I1 => ((sbyte)Marshal.ReadByte(pointer)).ToString(),
                    CorElementType.U1 => Marshal.ReadByte(pointer).ToString(),
                    CorElementType.I2 => Marshal.ReadInt16(pointer).ToString(),
                    CorElementType.U2 => ((ushort)Marshal.ReadInt16(pointer)).ToString(),
                    CorElementType.I4 => Marshal.ReadInt32(pointer).ToString(),
                    CorElementType.U4 => ((uint)Marshal.ReadInt32(pointer)).ToString(),
                    CorElementType.I8 => Marshal.ReadInt64(pointer).ToString(),
                    CorElementType.U8 => ((ulong)Marshal.ReadInt64(pointer)).ToString(),
                    CorElementType.R4 => BitConverter.Int32BitsToSingle(Marshal.ReadInt32(pointer)).ToString(),
                    CorElementType.R8 => BitConverter.Int64BitsToDouble(Marshal.ReadInt64(pointer)).ToString(),
                    _ => null,
                };
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
        catch
        {
            return null;
        }
    }

    private string MethodOf(CorDebugThread thread)
    {
        try
        {
            var function = thread.ActiveFrame.Function;
            var metadata = Extensions.GetMetaDataInterface<MetaDataImport>(function.Module);
            return metadata.GetMethodProps(function.Token).szMethod;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string DescribeMethod(CorDebugFrame frame)
    {
        try
        {
            var function = frame.Function;
            var metadata = Extensions.GetMetaDataInterface<MetaDataImport>(function.Module);
            var methodProps = metadata.GetMethodProps(function.Token);
            var typeProps = metadata.GetTypeDefProps(methodProps.pClass);
            return $"{typeProps.szTypeDef}.{methodProps.szMethod}";
        }
        catch
        {
            return "<unknown>";
        }
    }

    private static int ThreadId(CorDebugThread thread)
    {
        try { return thread.Id; }
        catch { return 0; }
    }

    private void Emit(
        DebugEventKind kind,
        string message,
        string method,
        int threadId,
        string file = "",
        int line = 0,
        int column = 0,
        SourceRange? requestedLocation = null,
        SourceRange? actualLocation = null,
        string breakpointId = "")
        => _events.Writer.TryWrite(new DebugEvent
        {
            Kind = kind,
            Message = message,
            MethodName = method,
            ThreadId = threadId,
            FilePath = file,
            Line = (uint)line,
            Column = (uint)Math.Max(0, column),
            RequestedLocation = requestedLocation,
            ActualLocation = actualLocation,
            BreakpointId = breakpointId,
        });

    private static T? Safe<T>(Func<T> f)
    {
        try { return f(); }
        catch { return default; }
    }
}

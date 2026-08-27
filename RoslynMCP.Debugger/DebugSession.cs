using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using ClrDebug;

namespace RoslynMCP.Debugger;

/// One ICorDebug debug session over a launched or attached .NET Framework debuggee. Ported from
/// docs/research/probes/IcorDebugProbe. All ICorDebug setup + client commands run on one
/// long-lived session thread (the runtime ties the debugger context to it); managed callbacks
/// arrive on the runtime's own thread and push events. The debuggee stops on every callback; we
/// auto-continue all but breakpoints/step-completes/pauses (which wait for an explicit Continue).
public sealed partial class DebugSession : IDebugSession
{
    public uint Id { get; }

    /// <summary>The debuggee's process id.</summary>
    /// <remarks>
    /// Setting it also takes a handle on the process, which is the only way its exit code can be
    /// read afterwards — see <see cref="DescribeExit"/> for why that matters. Done here rather
    /// than at each of the four places a session acquires its debuggee, so a fifth cannot forget.
    /// </remarks>
    public int Pid
    {
        get => _pid;
        private set
        {
            _pid = value;

            try
            {
                _debuggee?.Dispose();
                _debuggee = value > 0 ? System.Diagnostics.Process.GetProcessById(value) : null;
            }
            catch
            {
                // Gone already, or not ours to open. The exit is still reported, just unnamed.
                _debuggee = null;
            }
        }
    }

    private int _pid;

    /// <summary>
    /// A handle on the debuggee held for the life of the session, so its exit code survives it.
    /// </summary>
    private System.Diagnostics.Process? _debuggee;

    private readonly Channel<DebugEvent> _events = Channel.CreateUnbounded<DebugEvent>();
    private readonly List<BreakpointSpec> _specs = new();
    private readonly object _specLock = new();
    /// Active bound breakpoints keyed by "file|line" (source) or "type.method" (entry).
    private readonly ConcurrentDictionary<string, BoundBreakpoint> _bound = new();
    /// Source specs keyed by the ACTUAL bound line's SourceKey, for hit-count/condition checks
    /// (the bound line can differ from the requested line when it snapped to a later sequence
    /// point). Hit counters live beside them.
    private readonly ConcurrentDictionary<string, BreakpointSpec> _boundSpecs = new();
    private readonly ConcurrentDictionary<string, int> _hitCounts = new();
    /// The requested SourceKey mapped to the key the breakpoint actually bound under. Binding
    /// snaps to the next sequence point and rewrites the spec's line, so a removal issued for the
    /// line the client asked about would otherwise match nothing and leave the breakpoint armed.
    private readonly ConcurrentDictionary<string, string> _boundKeyByRequest = new();
    /// <summary>Local directory prefix mapped to the prefix the PDBs use for the same place,
    /// learned from the first source file whose checksum confirmed the pair. Applied to its
    /// siblings so the hashing is paid once per build root rather than once per file.</summary>
    private readonly ConcurrentDictionary<string, string> _pathRewrites = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingCollection<Action> _commands = new();

    /// <summary>
    /// Watches the thread every command below runs on.
    /// </summary>
    /// <remarks>
    /// Everything a client asks of this session is marshalled onto that one thread, so one stuck
    /// ICorDebug call stops the whole session with nothing to show for it. See
    /// <see cref="SessionWatchdog"/>: it reports and takes a dump, and never intervenes.
    /// </remarks>
    private readonly SessionWatchdog _watchdog;
    private readonly ManualResetEventSlim _ready = new();
    /// PDB readers are expensive to create; one per module path, session-thread only.
    /// How long Break All waits for the CLR to reach a point where it can suspend. Zero would
    /// return before the process is actually stopped, which is what a pause has to guarantee.
    private const int PauseTimeoutMs = 5000;

    /// Set when an ApplyChanges fails: the runtime and the debugger's metadata view may now
    /// disagree, and there is no rollback, so no further edit is accepted.
    private bool _encPoisoned;

    private readonly Dictionary<string, SymbolReader?> _readers = new(StringComparer.OrdinalIgnoreCase);
    /// Temporary PDBs written out for symbols the runtime handed us in memory: a Windows-format
    /// reader only takes a path, so the bytes have to live on disk for as long as the reader does.
    /// Deleted when the session ends. Concurrent because symbol updates arrive on mscordbi's
    /// callback thread while the session thread is free to be tearing down.
    private readonly ConcurrentBag<string> _spilledSymbolFiles = new();
    /// What the last attempt at a module's symbols found, keyed by module path. Kept beside the
    /// readers rather than derived from them, because the interesting cases are the ones where
    /// there is no reader to ask.
    private readonly ConcurrentDictionary<string, SymbolStatusEntry> _symbolStatus =
        new(StringComparer.OrdinalIgnoreCase);
    /// Decompiled source the host has handed over, per module: the symbols for modules that have
    /// none. Written from whichever thread the host pushes on and read on the session and callback
    /// threads, so both this and the sets inside it are concurrent.
    private readonly ConcurrentDictionary<string, DecompiledSymbolSet> _decompiledSymbols =
        new(StringComparer.OrdinalIgnoreCase);
    /// One reader per module over the sets above. Kept apart from <see cref="_readers"/>, which
    /// caches the answer to "does this module have a PDB" and must keep saying no.
    private readonly ConcurrentDictionary<string, SymbolReader> _decompiledReaders =
        new(StringComparer.OrdinalIgnoreCase);
    /// Readers that have been replaced or whose module unloaded, kept until the session ends.
    /// They are dropped on the runtime's callback thread while the session thread may still be
    /// reading sequence points through them, and closing a COM reader out from under that reader
    /// is a crash rather than a stale answer. A session sees a handful of these at most.
    private readonly ConcurrentBag<SymbolReader> _retiredReaders = new();
    /// Set once the readers have been closed for good, so a symbol update arriving during teardown
    /// does not open a reader nothing will ever close. Guarded by <c>_readers</c>.
    private bool _symbolsClosed;
    /// Steppers armed but not yet completed, each tagged with the thread it was armed on so a
    /// completion arriving for a different thread can be told apart from the user's own step.
    private readonly List<(int ThreadId, CorDebugStepper Stepper)> _steppers = new();
    private CorDebug? _corDebug;
    private CorDebugProcess? _process;
    private CorDebugThread? _stoppedThread;
    private SuspendedProcess? _child;
    /// Completed when the debuggee's process is gone, so a shutdown can wait for the real thing
    /// rather than guess at how long its <c>StopAsync</c> takes.
    private readonly TaskCompletionSource _exited =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? _launchError;
    private Thread? _thread;
    private DebugRuntime _runtime = DebugRuntime.NetFramework;
    private DbgShim? _dbgShim;
    private IntPtr _dbgShimModule;
    private RuntimeStartupCallback? _coreClrStartupCallback;
    private IntPtr _coreClrStartupCookie;
    /// Non-zero for attach sessions: drives CLR version discovery from the live target.
    private int _attachPid;
    /// <summary>Which exceptions suspend the target. Replaced wholesale rather than mutated, so
    /// the callback thread always reads one coherent policy.</summary>
    private volatile ExceptionPolicy _exceptionPolicy = ExceptionPolicy.Default;

    /// <summary>Whether the current stop is an exception stop — the only time the frame list
    /// carries a <c>$exception</c> row, exactly as VS's Locals window does.</summary>
    private volatile bool _stoppedOnException;
    /// Module that produced each bound breakpoint key, so an unload (app-domain recycle) can
    /// return those breakpoints to pending and let the next LoadModule rebind them.
    private readonly ConcurrentDictionary<string, string> _boundModule = new(StringComparer.OrdinalIgnoreCase);
    /// Non-framework modules by simple assembly name, JIT-flagged for EnC at load — the
    /// ApplyHotReload targets.
    private readonly ConcurrentDictionary<string, CorDebugModule> _encModules = new(StringComparer.OrdinalIgnoreCase);
    /// Modules already reported as symbol-less (one diagnostic per module, not per breakpoint).
    private readonly HashSet<string> _noSymbolsReported = new(StringComparer.OrdinalIgnoreCase);

    public DebugSession(uint id)
    {
        Id = id;
        // Narrated through the same event stream as everything else, so a wedged session says so
        // where the user is already looking rather than in a log they would have to know to read.
        _watchdog = new SessionWatchdog(message =>
            Emit(DebugEventKind.Diagnostic, message, string.Empty, 0));
    }

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
        // Binding touches ICorDebug state, which requires a synchronized process.
        Enqueue(() => WhileSynchronized("set a breakpoint", () =>
        {
            foreach (var module in LoadedModules())
                TryBindBreakpoint(module, spec);
        }));
    }

    /// <summary>
    /// Runs work that touches ICorDebug breakpoint state, suspending a running target just long
    /// enough to do it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both arming and disarming a breakpoint need a synchronized process. Adding one used to
    /// give up while the target ran, leaving a comment saying the next <c>LoadModule</c> or stop
    /// would pick it up; neither happens for a server, whose assemblies load during startup and
    /// which never stops on its own, so every breakpoint set after the app was up silently never
    /// bound. Removing one had the mirror image of the bug: the deactivate failed unsynchronized
    /// and was swallowed, so the native breakpoint stayed armed and the debuggee kept stopping at
    /// a breakpoint the user had deleted.
    /// </para>
    /// <para>
    /// The suspension is deliberately invisible: no <c>_stoppedThread</c>, no paused event, and
    /// <c>Continue</c> in a finally so a failure cannot leave the debuggee frozen. <c>Stop</c> is
    /// counted, so the pairing has to be exact — an unbalanced pair wedges every later resume.
    /// </para>
    /// </remarks>
    private void WhileSynchronized(string what, Action work)
    {
        if (_process is not { } process)
            return;

        // Names the work for the watchdog. Every command reaching here already says what it is
        // trying to do, so a report about a session that stopped responding can say what it stopped
        // responding in the middle of rather than that something did.
        _watchdog.Starting(what);

        if (_stoppedThread is not null)
        {
            work();
            return;
        }

        try
        {
            process.Stop(PauseTimeoutMs);
        }
        catch (Exception ex)
        {
            Emit(DebugEventKind.Diagnostic,
                $"could not suspend the target to {what}: {ex.Message}", string.Empty, 0);
            return;
        }

        try
        {
            work();
        }
        finally
        {
            try { process.Continue(false); }
            catch (Exception ex)
            {
                Emit(DebugEventKind.Diagnostic,
                    $"the target could not be resumed after trying to {what}: {ex.Message}",
                    string.Empty, 0);
            }
        }
    }

    public bool RemoveBreakpoint(string filePath, int line)
    {
        var requestedKey = SourceKey(filePath, line);
        var key = _boundKeyByRequest.TryGetValue(requestedKey, out var actualKey) ? actualKey : requestedKey;
        lock (_specLock)
        {
            _specs.RemoveAll(s =>
            {
                if (s.FilePath.Length == 0)
                    return false;
                var specKey = SourceKey(s.FilePath, (int)s.Line);
                return specKey == key || specKey == requestedKey;
            });
        }
        _boundKeyByRequest.TryRemove(requestedKey, out _);
        // Condition state and hit counts are keyed by the bound line; leaving them behind would
        // let a re-added breakpoint inherit the removed one's counting.
        _boundSpecs.TryRemove(key, out _);
        _boundModule.TryRemove(key, out _);
        _hitCounts.TryRemove(key, out _);

        // Both keys, because one line can bind to two. A module whose sequence point sits below the
        // requested line arms under the moved-to key while another module — a different build, or a
        // second project compiling the same file — arms at the line as asked; disarming only the one
        // the last bind reported would leave the other standing in the debuggee.
        var homes = new List<BoundBreakpoint>();
        if (_bound.TryRemove(key, out var bound))
            homes.Add(bound);
        if (key != requestedKey && _bound.TryRemove(requestedKey, out var atRequested))
            homes.Add(atRequested);
        if (homes.Count == 0)
            return false;

        Enqueue(() => WhileSynchronized("remove a breakpoint", () =>
        {
            foreach (var home in homes)
            {
                try { home.DeactivateAll(); }
                catch (Exception ex)
                {
                    Emit(DebugEventKind.Diagnostic,
                        $"a removed breakpoint could not be disarmed and may still stop the target: {ex.Message}",
                        string.Empty, 0);
                }
            }
        }));
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

            BreakpointSpec spec;
            BreakpointLocation? resolved;
            if (request.MethodToken != 0)
            {
                // The host already mapped a decompiled or fetched file to IL; no document in any
                // PDB names this file, so document resolution would only fail.
                var range = SourceRangeOf(location.FilePath, (int)location.Line, (int)location.Column);
                resolved = new BreakpointLocation
                {
                    Requested = range,
                    Actual = range.Clone(),
                    Verified = true,
                    Kind = BreakpointKind.Source,
                };
                spec = new BreakpointSpec
                {
                    Id = $"run-to:{Guid.NewGuid():N}",
                    FilePath = location.FilePath,
                    Line = location.Line,
                    Column = location.Column,
                    ModulePath = request.ModulePath,
                    MethodToken = request.MethodToken,
                    IlOffset = request.IlOffset,
                    Temporary = true,
                    Kind = BreakpointKind.Source,
                };
            }
            else
            {
                resolved = ResolveBestLocation(location.FilePath, (int)location.Line, (int)location.Column);
                if (resolved is null)
                    return new RunToLocationResponse { Ok = false, Error = "no executable location found" };

                spec = new BreakpointSpec
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
            }
            lock (_specLock)
                _specs.Add(spec);
            foreach (var module in LoadedModules())
                TryBindBreakpoint(module, spec);
            _stoppedThread = null;
            ReleaseReturnValue();
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

            if (request.MethodToken != 0)
            {
                // IL form, mapped by the host from a decompiled or fetched file: legal only when
                // the target method is the one the frame is already executing.
                if ((int)frame.Function.Token != request.MethodToken)
                    return new SetNextStatementResponse
                    {
                        Ok = false,
                        Error = "target is in a different method than the selected frame",
                    };
                try
                {
                    var ilHr = TryPrepareSetIP(thread, frame, request.IlOffset);
                    if (ilHr != HRESULT.S_OK)
                        return new SetNextStatementResponse { Ok = false, Error = DescribeSetIpFailure(ilHr) };
                    frame.SetIP(request.IlOffset);
                    var moved = SourceRangeOf(location.FilePath, (int)location.Line, (int)location.Column);
                    Emit(
                        DebugEventKind.Paused,
                        "set next statement",
                        DescribeMethod(frame),
                        ThreadId(thread),
                        moved.FilePath,
                        (int)moved.Line,
                        (int)moved.Column,
                        actualLocation: moved);
                    return new SetNextStatementResponse { Ok = true, Actual = moved };
                }
                catch (Exception ex)
                {
                    return new SetNextStatementResponse { Ok = false, Error = ex.Message };
                }
            }

            var target = ResolveBestLocationInFrame(frame, location.FilePath, (int)location.Line, (int)location.Column);
            if (target is null)
                return new SetNextStatementResponse
                {
                    Ok = false,
                    Error = "target is not a legal executable point in the selected method",
                };
            try
            {
                var hr = TryPrepareSetIP(thread, frame, target.Value.Offset);
                if (hr != HRESULT.S_OK)
                    return new SetNextStatementResponse { Ok = false, Error = DescribeSetIpFailure(hr) };
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

    /// <summary>
    /// Asks whether the IP can move to <paramref name="offset"/>, clearing an in-flight exception
    /// out of the way first when that is the only thing blocking it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Moving the instruction pointer is refused outright while an exception is unwinding, and
    /// that is exactly when it is most wanted: the user is stopped on a throw, has fixed the
    /// input, and wants to re-run the line. The runtime's own way out is
    /// <c>InterceptCurrentException</c> — stop the unwind at this frame and put the thread back on
    /// ordinary execution — after which the move is a normal one.
    /// </para>
    /// <para>
    /// Only attempted when the refusal was specifically about the exception, and only ever as a
    /// prelude to a move the runtime then agrees to: if the second answer is still no, the
    /// interception has changed nothing the user can see, because the thread was going to be
    /// resumed from this frame either way.
    /// </para>
    /// </remarks>
    private HRESULT TryPrepareSetIP(CorDebugThread thread, CorDebugILFrame frame, int offset)
    {
        var hr = frame.TryCanSetIP(offset);
        if (hr != HRESULT.CORDBG_E_SET_IP_NOT_ALLOWED_ON_EXCEPTION)
            return hr;

        var intercepted = Safe(() => thread.TryInterceptCurrentException(frame.Raw));
        if (intercepted is not HRESULT.S_OK)
        {
            // Nothing was changed, so report the original refusal rather than the one from the
            // rescue attempt — the user asked to move the IP, not to intercept an exception.
            return hr;
        }

        _stoppedOnException = false;
        return frame.TryCanSetIP(offset);
    }

    /// <summary>
    /// Turns the runtime's refusal to move the instruction pointer into a sentence that names the
    /// rule being broken.
    /// </summary>
    /// <remarks>
    /// The raw HRESULT names are close enough to English to look like an explanation and are not
    /// one — <c>CORDBG_E_CANT_SET_IP_OUT_OF_FINALLY_ON_WIN64</c> tells a user who already knows the
    /// answer that they were right. Each of these is a rule with a reason, and the reason is what
    /// makes the refusal actionable.
    /// </remarks>
    private static string DescribeSetIpFailure(HRESULT hr) => hr switch
    {
        HRESULT.CORDBG_E_SET_IP_NOT_ALLOWED_ON_EXCEPTION =>
            "the thread is unwinding an exception, and the runtime refused to stop the unwind at " +
            "this frame — move to a frame the exception is still passing through, or let it finish",
        HRESULT.CORDBG_E_SET_IP_NOT_ALLOWED_ON_NONLEAF_FRAME =>
            "the instruction pointer can only be moved in the frame on top of the stack; select " +
            "the innermost frame first",
        HRESULT.CORDBG_E_CANT_SET_IP_INTO_CATCH =>
            "the target is inside a catch block, which can only be entered by an exception",
        HRESULT.CORDBG_E_CANT_SET_IP_INTO_FINALLY =>
            "the target is inside a finally block, which can only be entered by leaving the try",
        HRESULT.CORDBG_E_CANT_SET_IP_OUT_OF_FINALLY or
        HRESULT.CORDBG_E_CANT_SET_IP_OUT_OF_FINALLY_ON_WIN64 =>
            "the instruction pointer is inside a finally block and cannot leave it — the runtime " +
            "would have no way to complete the unwind it is part of",
        HRESULT.CORDBG_E_CANT_SET_IP_OUT_OF_CATCH_ON_WIN64 =>
            "the instruction pointer is inside a catch block and cannot leave it on 64-bit",
        HRESULT.CORDBG_E_CANT_SETIP_INTO_OR_OUT_OF_FILTER =>
            "exception filters run in their own context; the instruction pointer cannot cross into " +
            "or out of one",
        HRESULT.CORDBG_E_SET_IP_IMPOSSIBLE =>
            "the runtime cannot construct a valid state at the target — the two positions do not " +
            "agree on what is on the stack",
        HRESULT.CORDBG_E_ILLEGAL_IN_OPTIMIZED_CODE =>
            "the method was JIT-optimized, and an optimized frame has no reliable mapping between " +
            "IL offsets and machine code; rebuild without optimization to move the pointer here",
        HRESULT.CORDBG_E_FUNCTION_NOT_IL =>
            "the frame is native code, which has no IL offsets to move between",
        HRESULT.CORDBG_S_INSUFFICIENT_INFO_FOR_SET_IP =>
            "the runtime has too little information about this method to move the pointer safely",
        HRESULT.CORDBG_E_PROCESS_TERMINATED =>
            "the process has exited",
        _ => $"the runtime refused the move ({hr})",
    };

    public void Continue() => Enqueue(() =>
    {
        _stoppedOnException = false;
        _stoppedThread = null;
        // The value the last step captured belongs to that step. Once the target runs on, the next
        // stop is somewhere else entirely, and a row still labelled with the old call would be
        // attached to a line that never made it.
        ReleaseReturnValue();
        try { _process?.Continue(false); } catch { }
    });

    /// <summary>
    /// Break All: suspends the debuggee and establishes the same stop state a breakpoint would.
    /// </summary>
    /// <remarks>
    /// The suspend is the easy half. What makes a stop usable is the context that comes with it —
    /// a current thread, a frame, a source location — and <c>ICorDebugProcess::Stop</c> supplies
    /// none of that on its own. Without adopting a thread here, everything that reads
    /// <c>_stoppedThread</c> (stacks, locals, evaluation, stepping, Edit and Continue) sees a
    /// process that is stopped but has nothing to look at, which is how this previously reported
    /// a pause with no location and refused every follow-up.
    /// </remarks>
    public void Pause() => Enqueue(() =>
    {
        var process = _process;
        if (process is null)
        {
            // Silence here strands the caller: it waits for a stop event that can never arrive.
            Emit(DebugEventKind.Paused, "there is no debuggee to suspend", string.Empty, 0);
            return;
        }

        // Stop is counted, so calling it on an already-stopped target leaves the count unbalanced
        // and every later Continue short of it — the session wedges. Re-report the stop instead.
        if (_stoppedThread is { } current)
        {
            var (currentFile, currentLine, currentColumn) = ThreadLocation(current);
            Emit(
                DebugEventKind.Paused, "paused", MethodOf(current), ThreadId(current),
                currentFile, currentLine, currentColumn);
            return;
        }

        try
        {
            process.Stop(PauseTimeoutMs);
        }
        catch (Exception ex)
        {
            Emit(DebugEventKind.Diagnostic, $"the target could not be suspended: {ex.Message}", string.Empty, 0);
            return;
        }

        // Suspending is a stop the user asked for, so any step still in flight is abandoned. An
        // armed stepper left behind here would complete on the next continue and stop the session
        // somewhere nobody asked for — and if the next continue happens to come from an
        // evaluation, that phantom stop strands the evaluation's caller waiting for a completion
        // the suspended process can no longer deliver.
        DeactivateSteppers();

        var thread = FindThreadForBreak();
        _stoppedThread = thread;

        if (thread is null)
        {
            // Suspended, but every thread is in native code — there is no managed frame to show.
            Emit(DebugEventKind.Paused, "paused (no managed code is executing)", string.Empty, 0);
            return;
        }

        var (file, line, column) = ThreadLocation(thread);
        Emit(DebugEventKind.Paused, "paused", MethodOf(thread), ThreadId(thread), file, line, column);
    });

    /// <summary>
    /// Picks the thread a break should land on: user code first, then any managed frame.
    /// </summary>
    /// <remarks>
    /// Breaking into a framework or runtime thread is technically a stop and practically useless —
    /// the user pressed pause to see their own code. A thread whose active frame maps to a source
    /// file is theirs; the rest are a fallback so a break still produces something.
    /// </remarks>
    private CorDebugThread? FindThreadForBreak()
    {
        CorDebugThread? fallback = null;

        foreach (var appDomain in Safe(() => _process?.AppDomains) ?? Array.Empty<CorDebugAppDomain>())
        {
            foreach (var thread in Safe(() => appDomain.Threads) ?? Array.Empty<CorDebugThread>())
            {
                if (Safe(() => thread.ActiveFrame) is null)
                    continue;

                var (file, _, _) = ThreadLocation(thread);
                if (file.Length > 0)
                    return thread;

                fallback ??= thread;
            }
        }

        return fallback;
    }

    /// <summary>Replaces the exception stop policy.</summary>
    public void SetExceptionPolicy(ExceptionPolicy policy) =>
        _exceptionPolicy = policy ?? ExceptionPolicy.Default;

    public void Step(StepKind kind) => Enqueue(() =>
    {
        var thread = _stoppedThread;
        if (thread is null)
            return;
        try
        {
            var frame = thread.ActiveFrame;
            // Whatever the last step found out is about the last step. Released before this one is
            // armed so the handle keeping that object alive is let go once per step, not once per
            // session.
            ReleaseReturnValue();
            var stepper = frame.CreateStepper();
            stepper.SetInterceptMask(CorDebugIntercept.INTERCEPT_NONE);
            stepper.SetUnmappedStopMask(CorDebugUnmappedStop.STOP_NONE);
            // The user's step, so it is the one that may be filtered. When the runtime can do it,
            // a step into a property that is twenty frames of framework code never surfaces as
            // twenty step completes to reject — it lands where the user expected, once.
            SetStepperJustMyCode(stepper, CanStepWithRuntimeJustMyCode(frame));
            _stepOutBudget = MaxStepOuts;
            // A stepper only ever completes on the thread it was armed on, so anything arriving
            // for another thread belongs to a stepper we no longer care about.
            var steppingThread = ThreadId(thread);
            _steppingThreadId = steppingThread;
            var source = frame is CorDebugILFrame ilFrame && TryGetSourceStepRange(ilFrame, out var range)
                ? (COR_DEBUG_STEP_RANGE?)range
                : null;
            _stepOrigin = source is { } origin && kind is not StepKind.Out
                ? new StepOrigin(
                    steppingThread,
                    Safe(() => (int?)frame.FunctionToken) ?? 0,
                    // The frame's own stack range, so "landed back where we started" means this
                    // activation and not merely this method. Without it a recursive call reads as
                    // no progress and the step walks down the recursion.
                    Safe(() => (ulong?)frame.StackRange.pStart) ?? 0,
                    origin,
                    kind == StepKind.Into)
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
            lock (_stepperLock)
                _steppers.Add((steppingThread, stepper));
            // After the stepper, so a failure to arm the step leaves nothing watching for a value
            // that is never going to be returned. Step Out is excluded: its statement is the
            // caller's, and the call it is returning from has not finished making its value yet.
            if (source is { } watched && kind is not StepKind.Out)
                ArmReturnProbes(frame, steppingThread, watched);
            _stoppedOnException = false;
            _stoppedThread = null;
            _process?.Continue(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[debug] step failed: {ex.Message}");
        }
    });

    /// The stopped thread's call stack, top-first. Empty when running.
    /// <summary>
    /// The call stack of one thread, top-first. Empty while running.
    /// </summary>
    /// <param name="threadId">Which thread to walk; <c>0</c> means the one the stop landed on.
    /// Any other suspended thread can be asked, which is the only way to see what the rest of a
    /// server was doing when a request stopped.</param>
    public Task<List<StackFrame>> StackTraceAsync(int threadId = 0) => InvokeAsync(() =>
    {
        var frames = new List<StackFrame>();
        var thread = threadId == 0 ? _stoppedThread : FindThreadById(threadId);
        if (thread is null)
            return frames;
        var index = 0u;
        foreach (var chain in Safe(() => thread.Chains) ?? Array.Empty<CorDebugChain>())
        {
            foreach (var frame in Safe(() => chain.Frames) ?? Array.Empty<CorDebugFrame>())
            {
                var (file, line, column, endLine, endColumn) = FrameSpan(frame);
                // Filled for every frame, not only the ones without source. External-source
                // resolution skips frames that already have a file, and a frame with source still
                // needs its module and token to be reported as an active statement.
                var (modulePath, methodToken, ilOffset) = FrameIdentity(frame);
                frames.Add(new StackFrame
                {
                    Index = index++,
                    Method = DescribeMethod(frame),
                    FilePath = file,
                    Line = (uint)line,
                    Column = (uint)column,
                    EndLine = (uint)Math.Max(0, endLine),
                    EndColumn = (uint)Math.Max(0, endColumn),
                    ThreadId = ThreadId(thread),
                    ModulePath = modulePath,
                    MethodToken = methodToken,
                    IlOffset = ilOffset,
                    IsNonUserCode = IsNonUserFrame(frame),
                });
                if (index >= 128)
                    return frames;
            }
        }
        return frames;
    });

    /// <summary>Finds a suspended thread by its runtime id.</summary>
    private CorDebugThread? FindThreadById(int threadId)
    {
        var process = _process;
        if (process is null)
            return null;
        foreach (var appDomain in Safe(() => process.AppDomains) ?? Array.Empty<CorDebugAppDomain>())
        {
            foreach (var thread in Safe(() => appDomain.Threads) ?? Array.Empty<CorDebugThread>())
            {
                if (ThreadId(thread) == threadId)
                    return thread;
            }
        }
        return null;
    }

    public Task<List<DebugThread>> ThreadsAsync() => InvokeAsync(() =>
    {
        var threads = new List<DebugThread>();
        var process = _process;
        if (process is null)
            return threads;
        var stoppedId = _stoppedThread is { } stopped ? ThreadId(stopped) : 0;
        foreach (var appDomain in Safe(() => process.AppDomains) ?? Array.Empty<CorDebugAppDomain>())
        {
            foreach (var thread in Safe(() => appDomain.Threads) ?? Array.Empty<CorDebugThread>())
            {
                var (file, line, _) = ThreadLocation(thread);
                var id = ThreadId(thread);
                threads.Add(new DebugThread
                {
                    Id = id,
                    Stopped = stoppedId != 0 && id == stoppedId,
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
            // Asking for the reader is what probes the symbols, so listing modules also answers
            // the question for every module that nothing has needed yet.
            var reader = ReaderFor(module, path);
            var status = _symbolStatus.TryGetValue(path, out var recorded)
                ? recorded
                : new SymbolStatusEntry(SymbolStatuses.NotProbed, "", "", "");

            modules.Add(new DebugModule
            {
                Name = Path.GetFileName(path),
                Path = path,
                SymbolsLoaded = reader is not null,
                // The file the symbols were really read from, which is not always the sibling the
                // name suggests — embedded and runtime-supplied symbols have no file at all.
                SymbolPath = status.Path,
                SymbolStatus = status.Status,
                SymbolOrigin = status.Origin,
                SymbolDetail = status.Detail,
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

        variables.AddRange(FrameVariables(ilFrame));
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
            // Invariant culture, so a value read out of the locals list parses back in on any
            // host, and '\'A\'' assigns the character rather than its opening quote.
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            object parsed = type switch
            {
                CorElementType.Boolean => bool.Parse(literal),
                CorElementType.Char => literal is ['\'', var quoted, '\''] ? quoted : literal.Length > 0 ? literal[0] : '\0',
                CorElementType.I1 => sbyte.Parse(literal, invariant),
                CorElementType.U1 => byte.Parse(literal, invariant),
                CorElementType.I2 => short.Parse(literal, invariant),
                CorElementType.U2 => ushort.Parse(literal, invariant),
                CorElementType.I4 => int.Parse(literal, invariant),
                CorElementType.U4 => uint.Parse(literal, invariant),
                CorElementType.I8 => long.Parse(literal, invariant),
                CorElementType.U8 => ulong.Parse(literal, invariant),
                CorElementType.R4 => float.Parse(literal, invariant),
                CorElementType.R8 => double.Parse(literal, invariant),
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

        if (spec.Condition.Length > 0)
        {
            bool stop;
            try
            {
                stop = EvaluateCondition(thread, spec.Condition, out var problem);
                if (problem.Length > 0)
                    ReportConditionProblem(spec, problem);
            }
            catch (Exception ex)
            {
                ReportConditionProblem(spec, ex.Message);
                stop = true;
            }

            if (!stop)
                return false;
        }

        // Counted after the condition, not before. Every editor's hit count means "times the
        // condition was true", and so does the netcoredbg emulation, which only ever sees stops
        // the condition already admitted. Counting arrivals here instead made the same breakpoint
        // behave differently on the two engines: a condition true on even iterations with a hit
        // condition of "= 3" needs the 3rd arrival and a true condition to coincide, which they
        // never do — so the breakpoint silently never fires.
        var hits = _hitCounts.AddOrUpdate(SourceKey(file, line), 1, (_, c) => c + 1);
        if (hits <= spec.SkipHits)
            return false;

        // A hit the rule excludes is not a stop the user ever sees, and deciding that here — on
        // the runtime's callback thread, inside the debuggee's suspend — costs one comparison.
        // The same decision made by the host costs a suspend, a round trip and a resume per hit.
        if (spec.HitCondition.Length > 0 && !BreakpointRules.HitConditionMet(spec.HitCondition, (int)hits))
            return false;

        if (spec.LogMessage.Length > 0)
        {
            Emit(
                DebugEventKind.Logpoint,
                InterpolateLogMessage(thread, spec.LogMessage),
                string.Empty, ThreadId(thread),
                spec.FilePath, (int)spec.Line);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Replaces <c>{expression}</c> placeholders in a logpoint message with the values they name
    /// in the frame the breakpoint fired in.
    /// </summary>
    /// <remarks>
    /// Resolved through the same path walker a condition uses, so it reads fields, array elements
    /// and locals without ever calling into the debuggee. That is deliberate: a logpoint runs on
    /// every hit, and a func-eval per hit would have to resume the target to make the call — which
    /// is exactly the cost this whole path exists to avoid. An expression it cannot read shows the
    /// reason in place rather than dropping the line.
    /// </remarks>
    private string InterpolateLogMessage(CorDebugThread thread, string message)
    {
        var ilFrame = Safe(() => thread.ActiveFrame) as CorDebugILFrame;
        var sb = new StringBuilder(message.Length);

        for (var i = 0; i < message.Length; i++)
        {
            // "{{" and "}}" are literal braces, as they are in every other message the editor
            // interpolates — without them a logpoint cannot print a brace at all.
            if (message[i] is '{' or '}' && i + 1 < message.Length && message[i + 1] == message[i])
            {
                sb.Append(message[i]);
                i++;
                continue;
            }

            if (message[i] != '{')
            {
                sb.Append(message[i]);
                continue;
            }

            var end = message.IndexOf('}', i + 1);
            if (end < 0)
            {
                sb.Append(message[i..]);
                break;
            }

            var expression = message[(i + 1)..end];
            sb.Append(ReadForLog(ilFrame, expression));
            i = end;
        }

        return sb.ToString();
    }

    private string ReadForLog(CorDebugILFrame? ilFrame, string expression)
    {
        if (ilFrame is null)
            return "<no managed frame>";

        try
        {
            var value = ResolvePath(ilFrame, expression, out var error);
            return value is null
                ? $"<{(error.Length > 0 ? error : $"'{expression}' could not be resolved here")}>"
                : DescribeValue(value, applyDisplay: false);
        }
        catch (Exception ex)
        {
            return $"<{ex.Message}>";
        }
    }

    /// <summary>
    /// Says once that a breakpoint's condition could not be honoured, and that the breakpoint is
    /// therefore stopping every time.
    /// </summary>
    /// <remarks>
    /// Stopping is the safe direction — a breakpoint that stops too often is an annoyance, one that
    /// silently never stops costs an afternoon — but doing it silently is what makes an unsupported
    /// condition look like a broken debugger. Reported once per breakpoint, since the alternative is
    /// a message on every hit of a breakpoint in a loop.
    /// </remarks>
    private void ReportConditionProblem(BreakpointSpec spec, string problem)
    {
        // Keyed by the condition and the problem, not by the breakpoint alone: a breakpoint id is
        // derived from its position, so a breakpoint removed and re-added at the same line with a
        // different bad condition would otherwise inherit the first one's silence — leaving the
        // user believing the new condition is being honoured.
        if (!_reportedConditionProblems.TryAdd($"{spec.Id}|{spec.Condition}|{problem}", 0))
            return;

        Emit(
            DebugEventKind.Diagnostic,
            $"breakpoint at {Path.GetFileName(spec.FilePath)}:{spec.Line} could not apply its " +
            $"condition '{spec.Condition}' and stopped anyway: {problem}",
            string.Empty, 0);
    }

    /// <summary>Breakpoints already reported as having an unusable condition.</summary>
    private readonly ConcurrentDictionary<string, byte> _reportedConditionProblems = new();

    /// <summary>Breakpoint/module pairs already reported as a binding failure.</summary>
    private readonly ConcurrentDictionary<string, byte> _reportedBindFailures = new();

    /// <summary>
    /// Characters that cannot appear in a path expression, and so mark a condition as using syntax
    /// the resolver does not implement — comparisons, arithmetic, boolean operators.
    /// </summary>
    private static readonly char[] UnsupportedConditionOperators = ['<', '>', '+', '*', '/', '%', '&', '|', '^', '~', '?'];

    /// <summary>
    /// Whether the right-hand side of a condition is something that can meaningfully be compared
    /// against a stringified value: a quoted string, a character, a number, or one of the keywords
    /// that render as themselves.
    /// </summary>
    private static bool IsComparableLiteral(string expected)
    {
        if (expected.Length == 0)
            return false;
        if (expected is "null" or "true" or "false" or "True" or "False")
            return true;
        if (expected.Length >= 2 &&
            ((expected[0] == '"' && expected[^1] == '"') || (expected[0] == '\'' && expected[^1] == '\'')))
        {
            return true;
        }

        // Numbers, including negative and fractional ones. A leading '-' never reaches here as an
        // operator, because subtraction would have been rejected on the left-hand side already.
        var digits = expected[0] is '-' or '+' ? expected[1..] : expected;
        return digits.Length > 0 && digits.All(c => char.IsAsciiDigit(c) || c is '.');
    }

    /// "path == literal" / "path != literal" compared against the stringified value; a bare path
    /// is truthy when it isn't null/false/0.
    private bool EvaluateCondition(CorDebugThread thread, string condition, out string problem)
    {
        problem = string.Empty;
        if (thread.ActiveFrame is not CorDebugILFrame ilFrame)
        {
            problem = "the stopped frame has no IL to evaluate against";
            return true;
        }

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

        // Only '==' and '!=' against a literal are implemented. Anything else — 'x > 5',
        // 'i % 3 == 0', 's.Contains("a")' — would otherwise be silently treated as a member path,
        // fail to resolve, and leave an unconditional breakpoint behind with no explanation.
        if (path.IndexOfAny(UnsupportedConditionOperators) >= 0)
        {
            problem = "only '==' and '!=' comparisons against a literal are supported";
            return true;
        }

        // The right-hand side is compared as text, so anything that is not a literal — 'i == max',
        // 'x == n + 1', 'count != list.Count' — is compared against the *name* rather than its
        // value and can never match. Left unreported that is the worst outcome available: with
        // '==' the breakpoint silently never stops, and with '!=' it silently always does.
        if (expected is not null && !IsComparableLiteral(expected))
        {
            problem = $"'{expected}' is not a literal, and only literals can be compared against";
            return true;
        }

        var value = ResolvePath(ilFrame, path, out var resolveError);
        if (value is null)
        {
            problem = resolveError.Length > 0 ? resolveError : $"'{path}' could not be resolved here";
            return true; // fail-open: stop rather than risk never stopping
        }
        var actual = DescribeValue(value, applyDisplay: false);
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
        _inspectionFrame = ilFrame;
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
                current = name == ExceptionMarker && CurrentExceptionValue() is { } thrown ? thrown
                    : name == ReturnMarker && _returnValue?.Handle is { } returned ? returned
                    : isCall ? null : RootValue(ilFrame, name, argNames, localNames);

                // Not an argument or local: it may be a member of `this`, which is how a member is
                // normally written inside an instance method.
                if (current is null && RootValue(ilFrame, "this", argNames, localNames) is { } self)
                    current = MemberValue(self, name, isCall, out error);

                // A local the compiler moved into a capture class still answers to its own name.
                if (current is null && !isCall)
                    current = CapturedValue(ilFrame, name, localNames);

                // A bare static of the frame's own type, written the way it is written in code.
                if (current is null && !isCall)
                    current = FrameStaticValue(ilFrame, name);

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
                // '$raw' and friends are the segments that are not members: they select which
                // *view* of the value the rest of the path walks through.
                current = name switch
                {
                    RawMarker or StaticsMarker => current,
                    ProxyMarker => ProxyValue(current!, out error),
                    ResultsMarker => EnumerableItems(current!, out error),
                    _ when name.StartsWith(MoreMarker, StringComparison.Ordinal) => current,
                    _ => MemberValue(current!, name, isCall, out error),
                };
                if (current is null)
                    return null;
            }

            foreach (var index in indexes)
            {
                current = IndexValue(current, index, out var indexError);
                if (current is null)
                {
                    error = indexError.Length > 0 ? indexError : $"cannot index into '{name}'";
                    return null;
                }
            }
        }
        return current;
    }

    /// <summary>
    /// Splits one path segment into its member name, any indexers, and whether it was written as
    /// a call — <c>Items[0]</c>, <c>grid[1,2]</c>, <c>pairs["a"]</c>, <c>ToString()</c>.
    /// </summary>
    /// <remarks>Each parsed index is an <c>int[]</c> (one entry per dimension) or a string key
    /// for a keyed indexer; <see cref="IndexValue"/> takes them apart again.</remarks>
    private static (string Name, List<object> Indexes, bool IsCall) ParseSegment(string segment)
    {
        // Only parameterless calls are supported; arguments would need to be evaluated too.
        var isCall = segment.EndsWith("()", StringComparison.Ordinal);
        if (isCall)
            segment = segment[..^2];

        var indexes = new List<object>();
        var bracket = segment.IndexOf('[');
        var name = bracket < 0 ? segment : segment[..bracket];
        while (bracket >= 0)
        {
            var close = segment.IndexOf(']', bracket);
            if (close < 0)
                break;
            var body = segment[(bracket + 1)..close];
            if (body.Length >= 2 && body[0] == '"' && body[^1] == '"')
            {
                indexes.Add(body[1..^1]);
            }
            else
            {
                var parts = body.Split(',');
                var numeric = new int[parts.Length];
                var parsed = true;
                for (var p = 0; p < parts.Length; p++)
                    parsed &= int.TryParse(parts[p], out numeric[p]);
                if (parsed)
                    indexes.Add(numeric);
            }
            bracket = segment.IndexOf('[', close);
        }
        return (name, indexes, isCall);
    }

    /// <summary>
    /// Applies one indexer: array element access for a numeric index (multi-dimensional included),
    /// and the type's own <c>get_Item</c> for anything else — a list's position, a dictionary's
    /// string key.
    /// </summary>
    private CorDebugValue? IndexValue(CorDebugValue value, object index, out string error)
    {
        error = string.Empty;
        var target = Safe(() => Dereference(value));

        if (index is int[] numeric)
        {
            if (target is CorDebugArrayValue array)
            {
                return Safe(() => numeric.Length == 1
                    ? array.GetElementAtPosition(numeric[0])
                    : array.GetElement(numeric.Length, numeric));
            }
            if (numeric.Length != 1)
            {
                error = "only an array takes a multi-dimensional index";
                return null;
            }
            return InvokeIndexer(value, CreateIntValue(numeric[0], ref error), ref error);
        }

        if (index is string key)
            return InvokeIndexer(value, RunEval(eval => eval.NewString(key), out error), ref error);

        return null;
    }

    private CorDebugValue? InvokeIndexer(CorDebugValue value, CorDebugValue? argument, ref string error)
    {
        if (argument is null)
            return null;
        if (FindMethod(value, "get_Item") is not { } found)
        {
            error = "the value has no indexer";
            return null;
        }
        return InvokeFunction(found.Function, [found.Instance, argument], out error);
    }

    /// <summary>An <c>int</c> in the debuggee holding <paramref name="number"/>, to hand to an
    /// indexer. Creating one is synchronous — the debuggee never runs for it.</summary>
    private CorDebugValue? CreateIntValue(int number, ref string error)
    {
        var thread = _stoppedThread;
        if (thread is null)
        {
            error = "not stopped";
            return null;
        }
        try
        {
            var created = thread.CreateEval().CreateValue(CorElementType.I4, null);
            var generic = Extensions.As<CorDebugGenericValue>(created);
            var pointer = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(pointer, number);
                generic.SetValue(pointer);
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
            return created;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
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

    /// A named field of an object value, searching the class hierarchy. The name matches directly,
    /// as an auto-property backing field, or as a state-machine hoisted local ("<name>5__N") — the
    /// three shapes the compiler stores a source name under.
    private static CorDebugValue? FieldValue(CorDebugValue value, string name)
    {
        var target = Safe(() => Dereference(value));
        if (target is not CorDebugObjectValue obj)
            return null;

        var backing = $"<{name}>k__BackingField";
        var hoisted = $"<{name}>5__";
        foreach (var (cls, metadata, typeDef) in TypeChain(value))
        {
            foreach (var field in Fields(metadata, typeDef))
            {
                var fieldName = Safe(() => metadata.GetFieldProps(field).szField);
                if (fieldName is null)
                    continue;
                if (fieldName == name || fieldName == backing ||
                    fieldName.StartsWith(hoisted, StringComparison.Ordinal))
                {
                    var result = Safe(() => obj.GetFieldValue(cls.Raw, field));
                    if (result is not null)
                        return result;
                }
            }
        }
        return null;
    }

    /// <summary>A named static field anywhere in the value's type chain, read against the frame
    /// the inspection is happening in.</summary>
    private CorDebugValue? StaticFieldValue(CorDebugValue value, string name)
    {
        var frame = _inspectionFrame;
        foreach (var (cls, metadata, typeDef) in TypeChain(value))
        {
            var token = Safe(() =>
            {
                var field = metadata.FindField(typeDef, name, IntPtr.Zero, 0);
                return (mdFieldDef?)field;
            });
            if (token is { } fieldToken && fieldToken.Rid != 0)
            {
                var result = Safe(() => cls.GetStaticFieldValue((int)fieldToken, frame?.Raw));
                if (result is not null)
                    return result;
            }
        }
        return null;
    }

    /// <summary>A local the compiler moved into a capture class, found by searching the frame's
    /// compiler-generated locals for a field with the user's name.</summary>
    private CorDebugValue? CapturedValue(
        CorDebugILFrame ilFrame, string name, Dictionary<int, string> localNames)
    {
        var locals = Safe(() => ilFrame.LocalVariables);
        if (locals is null)
            return null;
        for (var i = 0; i < locals.Length; i++)
        {
            if (localNames.TryGetValue(i, out var localName) && IsCompilerGeneratedName(localName) &&
                IsDisplayClass(locals[i]) && FieldValue(locals[i], name) is { } captured)
                return captured;
        }
        return null;
    }

    /// <summary>A static field of the frame's own declaring type, which is how a bare static name
    /// resolves inside the method that shares its type.</summary>
    private static CorDebugValue? FrameStaticValue(CorDebugILFrame ilFrame, string name)
    {
        try
        {
            var function = ilFrame.Function;
            var metadata = Extensions.GetMetaDataInterface<MetaDataImport>(function.Module);
            var declaring = (mdTypeDef)metadata.GetMethodProps(function.Token).pClass;
            var field = metadata.FindField(declaring, name, IntPtr.Zero, 0);
            if (field.Rid == 0)
                return null;
            var cls = function.Module.GetClassFromToken(declaring);
            return cls.GetStaticFieldValue((int)field, ilFrame.Raw);
        }
        catch
        {
            return null;
        }
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
        // An evaluation that was given up on can still finish later. When it does, the thread it
        // was occupying is usable again, so the block that its refusal to abort put on the session
        // is lifted rather than lasting until the session ends.
        if (_abandonedEval is { } abandoned && !IsDifferentEval(abandoned, eval))
        {
            _abandonedEval = null;
            _evalsDisabled = false;
            Emit(
                DebugEventKind.Diagnostic,
                "the evaluation that could not be aborted has finished; evaluation is available again",
                string.Empty, 0);
            return;
        }

        var pending = _pendingEval;
        if (pending is null)
            return;

        // Only the evaluation we are actually waiting on may complete the wait. An abandoned
        // evaluation — one that timed out and was aborted — still raises its completion later, and
        // without this check that stale callback hands its result to whichever evaluation is
        // running by then, so an unrelated member reads as the wrong value.
        if (IsDifferentEval(pending, eval))
            return;

        _evalFaulted = faulted;
        // On a fault the result is the exception object itself, which is worth keeping: "threw
        // NullReferenceException" narrows a failure in a way "threw an exception" never will.
        _evalResult = Safe(() => eval.Result);

        // This runs on the runtime's callback thread while the waiting caller runs on the session
        // thread, and that caller disposes the event as soon as its wait ends. Losing the race
        // means signalling a disposed event, which would throw out of a runtime callback and take
        // the whole session down — whereas the caller giving up is a perfectly ordinary outcome.
        try { _evalDone?.Set(); }
        catch (ObjectDisposedException) { }
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
        // A method declared on a generic type must be called with the instantiation's type
        // arguments — plain CallFunction makes the runtime throw TypeLoadException ("used with
        // the wrong number of generic arguments") inside the evaluation, which is how every
        // List<T>.Count on .NET Framework used to come back as an eval fault.
        var typeArguments = DeclaringTypeIsGeneric(function) && args.Length > 0
            ? TypeArgumentsOf(args[0])
            : null;

        return RunEval(
            eval =>
            {
                var raw = args.Select(a => a.Raw).ToArray();
                if (typeArguments is { Length: > 0 })
                    eval.CallParameterizedFunction(function.Raw, typeArguments.Length, typeArguments, raw.Length, raw);
                else
                    eval.CallFunction(function.Raw, raw.Length, raw);
            },
            out error);
    }

    private static bool DeclaringTypeIsGeneric(CorDebugFunction function) =>
        Safe(() =>
        {
            var metadata = Extensions.GetMetaDataInterface<MetaDataImport>(function.Module);
            return metadata.GetTypeDefProps(function.Class.Token).szTypeDef.Contains('`');
        }) == true;

    /// <summary>
    /// Runs one evaluation in the debuggee — a call, or a constructor for a debugger view type —
    /// and returns what it produced.
    /// </summary>
    /// <param name="start">Arms the evaluation. Called before the process is resumed; whatever it
    /// throws is reported rather than left to hang the waiting caller.</param>
    private CorDebugValue? RunEval(Action<CorDebugEval> start, out string error)
    {
        error = string.Empty;

        if (_evalsDisabled)
        {
            error = EvalsDisabledMessage;
            return null;
        }

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
            start(eval);
            process.Continue(false);

            if (!done.Wait(EvalTimeout))
            {
                var abandoned = AbandonEval(eval, done);
                if (abandoned.Length > 0)
                {
                    error = abandoned;
                    return null;
                }

                // It finished while we were giving up on it, so there is a real result to hand
                // back rather than a timeout to report.
            }

            if (_evalFaulted)
            {
                error = DescribeEvalFault(_evalResult);
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

    /// <summary>How many polite <c>Abort</c> attempts to make before escalating to a rude one.</summary>
    private const int GentleAbortAttempts = 3;

    /// <summary>Total abort attempts — the attempts past <see cref="GentleAbortAttempts"/> are rude.</summary>
    private const int MaxAbortAttempts = 6;

    /// <summary>How long to give each abort attempt to take effect.</summary>
    private static readonly TimeSpan AbortPollInterval = TimeSpan.FromMilliseconds(200);

    private const string EvalsDisabledMessage =
        "evaluation is disabled for this session: an earlier evaluation could not be aborted, so " +
        "the thread it was running on can no longer host one. Restart the session to inspect values.";

    /// <summary>Set once an evaluation could not be aborted, which poisons evaluation session-wide.</summary>
    private bool _evalsDisabled;

    /// <summary>
    /// Gives up on an evaluation that outran its timeout, and reports what that cost.
    /// </summary>
    /// <remarks>
    /// A single best-effort <c>Abort</c> is not enough. <c>Abort</c> asks the runtime to unwind the
    /// evaluation at its next safe point, and code that never reaches one — a tight loop, a blocked
    /// wait — simply ignores it. <c>RudeAbort</c> abandons the frames instead of unwinding them,
    /// which is more likely to land but leaves any lock the evaluation held permanently taken.
    /// Hence the order: ask politely a few times first, and escalate only when that fails.
    ///
    /// If nothing lands, the evaluation is still live on its thread and every later
    /// <c>CreateEval</c> there will fail. Saying so once, and refusing further evaluations, is far
    /// more useful than letting each subsequent inspection fail on its own with an opaque message.
    /// </remarks>
    /// <returns>The error to report to the caller.</returns>
    private string AbandonEval(CorDebugEval eval, ManualResetEventSlim done)
    {
        // The evaluation may have finished in the moment between the wait expiring and this call,
        // in which case there is nothing to abort and a result is already waiting.
        if (done.IsSet)
            return string.Empty;

        for (var attempt = 0; attempt < MaxAbortAttempts; attempt++)
        {
            var rude = attempt >= GentleAbortAttempts;

            // These are the HRESULT-returning overloads: they report failure rather than throwing,
            // so the result has to be inspected. A dead process is the case worth separating —
            // there is nothing left to abort, and no completion will ever arrive to end the loop.
            HRESULT hr;
            try
            {
                hr = rude ? eval.TryRudeAbort() : eval.TryAbort();
            }
            catch (Exception ex)
            {
                hr = (HRESULT)ex.HResult;
            }

            if (hr is HRESULT.CORDBG_E_PROCESS_TERMINATED or HRESULT.CORDBG_E_OBJECT_NEUTERED)
                return "the process exited before the evaluation finished";

            // The abort completes through the ordinary completion callback, so waiting on the same
            // event is how we learn it worked.
            if (done.Wait(AbortPollInterval))
            {
                return rude
                    ? "the evaluation timed out and had to be abandoned mid-call; the target may " +
                      "hold locks that will not be released"
                    : "the evaluation timed out and was aborted";
            }
        }

        // Remembered so its eventual completion — if the call ever does end — can lift the block
        // rather than leaving the session permanently uninspectable over one slow call.
        _abandonedEval = eval;
        _evalsDisabled = true;
        Emit(
            DebugEventKind.Diagnostic,
            "an evaluation could not be aborted; evaluation is disabled until that call ends or " +
            "the session restarts",
            string.Empty, 0);
        return EvalsDisabledMessage;
    }

    /// <summary>The evaluation that could not be aborted, kept so its late completion can be seen.</summary>
    private CorDebugEval? _abandonedEval;

    /// <summary>
    /// Whether a completion belongs to some evaluation other than the one being waited on.
    /// </summary>
    /// <remarks>
    /// Deliberately answers "no" whenever it cannot prove otherwise. The wrapper handed to the
    /// callback is not the instance we started, so identity has to come from the COM object
    /// underneath — and if that cannot be established, treating the completion as unrelated would
    /// strand the waiting caller until its timeout. Failing open costs the stale-result bug this
    /// check exists to prevent; failing closed would hang every evaluation instead. Only a positive
    /// mismatch is acted on.
    /// </remarks>
    private static bool IsDifferentEval(CorDebugEval pending, CorDebugEval completed)
    {
        if (ReferenceEquals(pending, completed))
            return false;

        object left = pending.Raw;
        object right = completed.Raw;
        if (ReferenceEquals(left, right))
            return false;

#pragma warning disable CA1416 // ICorDebug, and therefore this whole session, is Windows-only.
        // Pointer identity is meaningful only if these really are COM objects; otherwise the
        // comparison below would be comparing wrappers and every completion would look unrelated.
        if (!Marshal.IsComObject(left) || !Marshal.IsComObject(right))
            return false;

        var leftUnknown = IntPtr.Zero;
        var rightUnknown = IntPtr.Zero;
        try
        {
            leftUnknown = Marshal.GetIUnknownForObject(left);
            rightUnknown = Marshal.GetIUnknownForObject(right);
            return leftUnknown != rightUnknown;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (leftUnknown != IntPtr.Zero)
                Marshal.Release(leftUnknown);
            if (rightUnknown != IntPtr.Zero)
                Marshal.Release(rightUnknown);
        }
#pragma warning restore CA1416
    }

    /// <summary>
    /// Whether an exception the runtime just reported is one the session should stop on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The caught/uncaught split comes from the runtime's own <c>unhandled</c> flag rather than
    /// from the richer v2 exception callback. The v2 callback names four event types and would
    /// distinguish "a handler was found" from "thrown, outcome unknown" — but it also fires once
    /// per frame the exception unwinds through, and the ordering between it and the v1 callback is
    /// not something to build a stop decision on. One callback, fired once per throw, with a flag
    /// that is already correct at second chance, is the version that cannot double-stop.
    /// </para>
    /// <para>
    /// Fails open on a type that cannot be read: an exception whose type is unknown is admitted by
    /// an include list rather than silently dropped, because the failure mode of the other choice
    /// is a breakpoint-shaped hole the user cannot see.
    /// </para>
    /// </remarks>
    private bool ShouldStopOnException(bool unhandled, CorDebugValue? thrown)
    {
        // Each class of exception carries its own type lists: narrowing "break on every throw" to
        // one type must not also narrow which unhandled exceptions stop the process.
        var rule = unhandled ? _exceptionPolicy.Unhandled : _exceptionPolicy.Caught;

        if (!rule.Enabled)
            return false;

        if (rule.IncludeTypes.Count == 0 && rule.ExcludeTypes.Count == 0)
            return true;

        var names = ExceptionTypeNames(thrown);
        if (names.Count == 0)
            return true;

        if (rule.ExcludeTypes.Count > 0 && rule.ExcludeTypes.Any(f => MatchesAnyType(names, f)))
            return false;

        return rule.IncludeTypes.Count == 0 || rule.IncludeTypes.Any(f => MatchesAnyType(names, f));
    }

    /// <summary>
    /// The thrown value's type and every base type above it, so a filter naming a base class
    /// catches the derived ones — which is what "break on IOException" is normally asking for.
    /// </summary>
    private static List<string> ExceptionTypeNames(CorDebugValue? thrown)
    {
        var names = new List<string>();
        if (thrown is null)
            return names;

        foreach (var (_, metadata, typeDef) in TypeChain(thrown))
        {
            var name = Safe(() => metadata.GetTypeDefProps(typeDef).szTypeDef);
            if (name is { Length: > 0 })
                names.Add(name);
        }

        return names;
    }

    /// <summary>Matches a filter against a type name by full name or by simple name, so both
    /// <c>System.IO.IOException</c> and <c>IOException</c> work.</summary>
    private static bool MatchesAnyType(List<string> names, string filter)
    {
        var wanted = filter.Trim();
        if (wanted.Length == 0)
            return false;

        foreach (var name in names)
        {
            if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
                return true;

            var dot = name.LastIndexOf('.');
            if (dot >= 0 && string.Equals(name[(dot + 1)..], wanted, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// How a thrown exception should read: its type and message, with the stage appended.
    /// </summary>
    /// <remarks>
    /// The type has to lead. The host parses the type back out of this message to fill the
    /// editor's exception popup, and before this it was parsing a fixed string — so every
    /// exception, whatever it was, was reported to the user as "exception (unhandled)".
    /// </remarks>
    private string DescribeThrownException(CorDebugValue? thrown, bool unhandled)
    {
        var stage = unhandled ? "unhandled" : "first chance";

        if (thrown is null)
            return $"exception ({stage})";

        var type = Safe(() => TypeNameOf(thrown)) ?? string.Empty;
        if (type.Length == 0)
            return $"exception ({stage})";

        var message = Safe(() => FieldValue(thrown, "_message")) is { } messageField
            ? Safe(() => DescribeValue(messageField, applyDisplay: false))
            : null;

        return message is { Length: > 0 } and not "null"
            ? $"{type}: {message.Trim('"')} ({stage})"
            : $"{type} ({stage})";
    }

    /// <summary>What a faulted evaluation should report: the thrown exception's type and message
    /// when they can be read, rather than the fact that something somewhere threw.</summary>
    private string DescribeEvalFault(CorDebugValue? exception)
    {
        if (exception is null)
            return "the evaluated member threw an exception";

        var type = TypeNameOf(exception);
        if (type.Length == 0)
            return "the evaluated member threw an exception";

        var message = Safe(() => FieldValue(exception, "_message")) is { } messageField
            ? DescribeValue(messageField, applyDisplay: false)
            : null;

        return message is { Length: > 0 } and not "null"
            ? $"the evaluated member threw {type}: {message}"
            : $"the evaluated member threw {type}";
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
        if (target is not CorDebugObjectValue)
            return null;

        foreach (var (cls, metadata, typeDef) in TypeChain(value))
        {
            var token = Safe(() =>
            {
                var method = metadata.FindMethod(typeDef, name, IntPtr.Zero, 0);
                return (mdMethodDef?)method;
            });

            if (token is { } methodToken)
            {
                var function = Safe(() => cls.Module.GetFunctionFromToken(methodToken));
                if (function is not null)
                    return (function, value);
            }
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
        if (!callOnly && StaticFieldValue(value, name) is { } staticField)
            return staticField;

        // A computed property has no backing field, so it has to be invoked.
        foreach (var candidate in callOnly ? [name] : new[] { $"get_{name}", name })
        {
            if (FindMethod(value, candidate) is not { } found)
                continue;

            // A static getter takes no instance; handing it one faults the evaluation.
            var isStatic = Safe(() =>
            {
                var metadata = Extensions.GetMetaDataInterface<MetaDataImport>(found.Function.Module);
                return metadata.GetMethodProps(found.Function.Token).pdwAttr.HasFlag(CorMethodAttr.mdStatic);
            }) == true;

            // A getter that only returns a field can be read rather than run. Worth checking
            // first because the alternative is not merely slower: every func-eval resumes the
            // debuggee, and expanding one object with twenty such properties means twenty
            // resumes, each of which can hit a breakpoint, deadlock on a lock the stopped thread
            // holds, or time out.
            if (!isStatic && InterpretFieldGetter(found.Instance, found.Function) is { } interpreted)
                return interpreted;

            var result = InvokeFunction(found.Function, isStatic ? [] : [found.Instance], out error);
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

    /// <summary>
    /// Reads the field a trivial getter would have returned, without running it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only two IL shapes are recognised, both of them exactly "return this field": the release
    /// form the compiler emits for an auto-property or a one-line getter, and the debug form,
    /// which stores to a local and branches to a return so a breakpoint on the closing brace has
    /// somewhere to land. Anything else — a null check, a lazy initializer, a computed
    /// expression — falls through to a real call, because interpreting it would mean guessing at
    /// what the method does.
    /// </para>
    /// <para>
    /// Reading the field is not just faster than calling the getter, it is safer. A func-eval has
    /// to resume the debuggee to make the call, which means every property expansion is a chance
    /// to hit a breakpoint, block on a lock the stopped thread is holding, or run a side effect
    /// the user did not ask for.
    /// </para>
    /// </remarks>
    private CorDebugValue? InterpretFieldGetter(CorDebugValue instance, CorDebugFunction getter)
    {
        var il = Safe(() =>
        {
            var code = getter.ILCode;
            return code.GetCode(0, code.Size, code.Size);
        });

        if (il is null || FieldTokenOfTrivialGetter(il) is not { } fieldToken)
            return null;

        // The field is read on the type that declares the getter, which is not necessarily the
        // value's most-derived type when the property was inherited.
        var declaring = Safe(() => getter.Class);
        if (declaring is null)
            return null;

        return Safe(() => Dereference(instance) is CorDebugObjectValue obj
            ? obj.GetFieldValue(declaring.Raw, new mdFieldDef(fieldToken))
            : null);
    }

    /// <summary>
    /// The <c>ldfld</c> token of a method body that does nothing but return an instance field, or
    /// null when the body is anything else.
    /// </summary>
    /// <remarks>
    /// <c>ICorDebugCode::GetCode</c> addresses IL by IL offset, so what comes back is the bare
    /// instruction stream with no method header in front of it. The match is written against
    /// exact lengths rather than a prefix so that a longer body — a getter that does anything
    /// else at all — cannot pass by looking like one of these at the start.
    /// </remarks>
    private static int? FieldTokenOfTrivialGetter(byte[] il)
    {
        const byte Ldarg0 = 0x02, Ldfld = 0x7B, Ret = 0x2A;
        const byte Nop = 0x00, Stloc0 = 0x0A, BrS = 0x2B, Ldloc0 = 0x06;

        // Release: ldarg.0; ldfld <field>; ret
        if (il.Length == 7 && il[0] == Ldarg0 && il[1] == Ldfld && il[6] == Ret)
            return BitConverter.ToInt32(il, 2);

        // Debug: nop; ldarg.0; ldfld <field>; stloc.0; br.s +0; ldloc.0; ret — the local and the
        // branch exist so a breakpoint on the closing brace has an instruction to bind to.
        if (il.Length == 12 && il[0] == Nop && il[1] == Ldarg0 && il[2] == Ldfld &&
            il[7] == Stloc0 && il[8] == BrS && il[9] == 0x00 &&
            il[10] == Ldloc0 && il[11] == Ret)
        {
            return BitConverter.ToInt32(il, 3);
        }

        return null;
    }

    /// <summary>The exception this session is stopped on, or null at any other kind of stop.</summary>
    private CorDebugValue? CurrentExceptionValue() =>
        _stoppedOnException ? Safe(() => _stoppedThread?.CurrentException) : null;

    /// <summary>The frame the current inspection is rooted in — the statics context for
    /// <see cref="StaticFieldValue"/>, which cannot thread a frame through every member path.</summary>
    private CorDebugILFrame? _inspectionFrame;

    private static CorDebugValue Dereference(CorDebugValue value)
    {
        if (value is CorDebugReferenceValue reference && Safe(() => (bool?)reference.IsNull) != true)
            value = Safe(() => reference.Dereference()) ?? value;
        // A boxed value describes as its contents, not as "the box" — an `object` local holding
        // 42 reads 42, exactly as if it had never been boxed.
        if (value is CorDebugBoxValue box)
            return Safe(() => box.Object) ?? value;
        return value;
    }

    /// <summary>
    /// Ends the session by letting the debuggee shut itself down, and terminates it only if that
    /// does not finish in time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Terminate"/> kills the process where it stands: no <c>finally</c> blocks, no
    /// <c>Dispose</c>, and — the reason this exists — no <c>StopAsync</c> on any hosted service,
    /// so an app under the debugger never gets the orderly shutdown it gets everywhere else.
    /// </para>
    /// <para>
    /// The order matters. The target is almost always sitting at a breakpoint when a session is
    /// stopped, so its breakpoints are disarmed and its exception policy relaxed before it is
    /// resumed — otherwise the shutdown path would trap on the first breakpoint it crossed and
    /// the process would sit there until the timeout killed it.
    /// </para>
    /// </remarks>
    /// <returns>Whether the debuggee exited on its own, and why not when it did not.</returns>
    public async Task<(bool Graceful, string Error)> ShutdownAsync(TimeSpan timeout)
    {
        var child = _child;
        if (child is null)
        {
            // An attached process was not ours to start and is not ours to end; the caller should
            // be detaching from it instead.
            Terminate();
            return (false, "the debuggee was attached to, not launched by this session");
        }

        if (_thread is not null && !_exited.Task.IsCompleted)
        {
            try
            {
                await InvokeAsync(() =>
                {
                    // Nothing may trap on the way out — including the unhandled exception a host
                    // shutting down under a cancellation can legitimately end on.
                    _exceptionPolicy = new ExceptionPolicy { Unhandled = new ExceptionRule() };

                    foreach (var bound in _bound.Values)
                        bound.DeactivateAll();
                    _bound.Clear();
                    _boundSpecs.Clear();
                    DeactivateSteppers();
                    lock (_specLock) _specs.Clear();

                    _stoppedThread = null;
                    try { _process?.Continue(false); } catch { /* already running */ }
                    return true;
                }).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                Terminate();
                return (false, $"the debuggee could not be resumed to shut down: {ex.Message}");
            }
        }

        if (!child.RequestShutdown())
        {
            Terminate();
            return (false, "the debuggee could not be signalled to shut down");
        }

        var exited = await Task.WhenAny(_exited.Task, Task.Delay(timeout)) == _exited.Task;

        // Either way the session is over: on the graceful path this only reclaims the debugging
        // interface, which the exit callback does not do on its own.
        Terminate();

        return exited
            ? (true, string.Empty)
            : (false, $"the debuggee did not exit within {timeout.TotalSeconds:0.#}s and was terminated");
    }

    public void Terminate()
    {
        Enqueue(() =>
        {
            var process = _process;
            // Before the terminate, while the handle can still be disposed at all. A killed target
            // makes this moot, but this path is also how a session that turns out to be detachable
            // ends, and a handle released here can never be one left behind.
            ReleaseReturnValue();
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

        // A debuggee that exited on its own closed the command queue from its exit callback, so
        // the work queued above never ran. The interface must still be released — see
        // ShutdownCorDebug for what a session that skips it does to the next one — and with the
        // session thread joined and the target gone, nothing is left to race here.
        ShutdownCorDebug();

        // Last, because the thread it watches is only certainly finished once the join above has
        // returned — and a watchdog disposed early would have nothing to say about a teardown that
        // is itself where the session hung.
        _watchdog.Dispose();
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

        // After the readers are closed, not before: diasymreader keeps the file open for as long as
        // the reader lives, so deleting first would fail and leave the file behind for good.
        DisposeSymbolReaders();

        // The exit code has been read by now if it was ever going to be — the exit event is what
        // brings a session here — so the handle held for it can go.
        try { _debuggee?.Dispose(); } catch { }
        _debuggee = null;

        if (corDebug is null)
            return;

        try { corDebug.Terminate(); } catch { }
    }

    /// <summary>
    /// Closes every symbol reader and removes the PDBs that were spilled to disk for them.
    /// </summary>
    /// <remarks>
    /// The spilled files are the ones the runtime handed over in memory — a dynamic or edited
    /// module's symbols, which exist nowhere on disk of their own accord. Without this, every
    /// debug session of a hot-reloading target leaves a PDB in the temp directory forever.
    /// </remarks>
    private void DisposeSymbolReaders()
    {
        SymbolReader?[] readers;
        lock (_readers)
        {
            readers = [.. _readers.Values];
            _readers.Clear();
            _symbolsClosed = true;
        }

        foreach (var reader in readers)
        {
            try { reader?.Dispose(); } catch { }
        }

        while (_retiredReaders.TryTake(out var retired))
        {
            try { retired.Dispose(); } catch { }
        }

        // After the readers, never before: diasymreader holds the file open until its reader is
        // destroyed, so deleting first would silently fail and leave the PDB behind for good.
        while (_spilledSymbolFiles.TryTake(out var path))
            SymbolReader.TryDelete(path);
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
            foreach (var bound in _bound.Values)
                bound.DeactivateAll();
            _bound.Clear();
            _boundSpecs.Clear();
            DeactivateSteppers();
            // The handle keeping the last step's return value readable is a strong handle in the
            // debuggee, and the debuggee survives this. Left behind it pins that object and
            // everything it reaches for the life of the process — with the debugging interface
            // gone, nothing can ever dispose it — and an outstanding handle is one more thing
            // Detach itself can refuse over.
            ReleaseReturnValue();
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
                var moduleName = Safe(() => e.Module.Name) ?? string.Empty;

                // The ASP.NET page compiler produces App_Web_*.dll by the dozen per page, and
                // each one processed here costs a PDB probe per breakpoint plus an EnC JIT flag
                // in the debuggee. Only inline markup code ever lives in them, so unless a
                // breakpoint targets a markup file they are skipped outright — TryBindBreakpoint
                // applies the same rule per spec. The site's own assemblies are shadow-copied
                // under the same "Temporary ASP.NET Files" root but keep their names, and stay
                // fully processed.
                var generated = IsGeneratedWebFormsModule(moduleName);
                if (moduleName.Length > 0 && !generated)
                    Emit(DebugEventKind.Module, moduleName, string.Empty, 0);
                // Generated page assemblies are never edit targets, but they still get the EnC
                // JIT flag inside RegisterEncModule: ApplyChanges runs against the whole
                // process, and an unflagged module is a way for it to fault rather than fail.
                RegisterEncModule(e.Module, moduleName, generated);
                MarkJustMyCode(e.Module, moduleName);
                foreach (var spec in SpecsSnapshot())
                    TryBindBreakpoint(e.Module, spec);
                if (!generated)
                    ReportMissingSymbols(e.Module, moduleName);
            };
            callback.OnUpdateModuleSymbols += (_, e) =>
            {
                // Symbols the runtime is handing over for a module that has none on disk. This is
                // the only route to source-level debugging of anything generated at runtime, so
                // the reader is replaced and every pending breakpoint is offered the module again.
                OnSymbolsUpdated(e.Module, e.SymbolStream);
            };
            callback.OnUnloadModule += (_, e) =>
            {
                // App-domain recycle / plugin unload: bound breakpoints in this module go back
                // to pending; the specs stay, so the next LoadModule rebinds them.
                var moduleName = Safe(() => e.Module.Name) ?? string.Empty;
                if (moduleName.Length > 0)
                    OnModuleUnloaded(e.Module, moduleName);
            };
            callback.OnBreakpoint += (_, e) =>
            {
                // The engine's own breakpoint, armed to read what a call returned before the value
                // stops being readable. Not a stop: the user pressed Step, and the step goes on.
                if (TryCaptureReturnValue(e.Thread, e.Breakpoint))
                {
                    try { e.Controller.Continue(false); } catch { }
                    return;
                }

                // An evaluation is in flight, and it runs the whole process to get its answer. Any
                // other thread reaching a breakpoint meanwhile is not a stop the user asked for,
                // and recording it would be fatal rather than merely wrong: the stop leaves the
                // process suspended, the evaluation's completion can then never arrive, and the
                // abort that follows the timeout cannot land in a suspended process either — which
                // ends with evaluation disabled for the rest of the session. The breakpoint stays
                // armed, so the next genuine hit stops normally.
                if (_pendingEval is not null)
                {
                    try { e.Controller.Continue(false); } catch { }
                    return;
                }
                // The runtime can deliver a breakpoint event whose thread has already run on —
                // observed against IIS Express when a breakpoint arms while requests are in
                // flight: the callback arrives after the response went out, and the thread's
                // active frame is thread-pool idling, not the breakpoint's method. Stopping
                // there would show the user a stop with no position and hand Edit-and-Continue
                // a stop shape with no user frame. Resume instead; the breakpoint stays armed
                // and the next genuine hit stops normally.
                if (!ThreadIsAtBreakpoint(e.Thread, e.Breakpoint))
                {
                    Emit(DebugEventKind.Diagnostic,
                        "a breakpoint event arrived after its thread had moved on; resuming", string.Empty, 0);
                    try { e.Controller.Continue(false); } catch { }
                    return;
                }
                var (file, line, column) = ThreadLocation(e.Thread);
                if (!ShouldStopAt(e.Thread, file, line))
                {
                    // Swallowed by a condition, a hit count, or a logpoint that has already
                    // logged: resume before OnAnyEvent sees the stop, so nothing outside this
                    // process ever learns the target suspended.
                    try { e.Controller.Continue(false); } catch { }
                    return;
                }
                var bound = TryGetBoundSpec(file, line, out var spec);
                if (bound && spec.Temporary)
                    RemoveBreakpoint(file, line);
                // The breakpoint, not the step, decides where this stop is. Any step still in
                // flight is now abandoned, so cancel its stepper rather than leaving it armed to
                // fire after the user's next continue.
                DeactivateSteppers();
                _stoppedOnException = false;
                _stoppedThread = e.Thread;
                // A breakpoint hit is the stop shape ApplyChanges is safe from, so edits that
                // arrived while the target was running land at this stop. Enqueued rather than
                // applied here: ApplyChanges must run on the session thread, not mscordbi's
                // callback thread, and the command queue's FIFO order still puts the flush
                // ahead of any continue or step the user issues after seeing the stop.
                Enqueue(FlushPendingDeltas);
                // Without the id the client cannot tell which breakpoint stopped it, which is
                // what hit conditions, logpoints and value watches are keyed by.
                Emit(
                    DebugEventKind.Breakpoint, "breakpoint hit", MethodOf(e.Thread), ThreadId(e.Thread),
                    file, line, column, breakpointId: bound ? spec.Id : string.Empty);
            };
            // After an edit, a thread still executing the old version of an edited method raises
            // this at the next remap point. Jumping it moves the frame onto the new version, so
            // a long-running loop picks the edit up without having to leave the method — without
            // the jump, the runtime's documented fallback applies and the frame completes on the
            // old code, which is exactly what this session did before it handled the callback.
            callback.OnFunctionRemapOpportunity += (_, e) =>
            {
                try
                {
                    if (e.Thread.ActiveFrame is CorDebugILFrame ilFrame &&
                        ilFrame.TryRemapFunction(RemapOffsetFor(e.NewFunction, e.OldILOffset)) == HRESULT.S_OK)
                    {
                        Emit(DebugEventKind.Diagnostic,
                            $"hot reload: {DescribeMethod(ilFrame)} jumped to its edited version",
                            string.Empty, 0);
                    }
                }
                catch
                {
                    // The frame finishes the old version — the pre-remap behaviour.
                }
                // OnAnyEvent's catch-all resumes the process after this returns.
            };

            callback.OnStepComplete += (_, e) =>
            {
                // A step completing while an evaluation is in flight is never the user's step: the
                // evaluation resumed the process, and a stepper armed before it can land in the
                // middle. Recording a stop here would leave the process suspended with the
                // evaluation's caller waiting on a completion that can no longer arrive.
                if (_pendingEval is not null)
                {
                    try { e.Controller.Continue(false); }
                    catch { /* already continued / terminated */ }
                    return;
                }

                // A completion for a thread other than the one the user stepped on is not their
                // step: it belongs to a stepper left over from an earlier one. Reporting it would
                // stop the session somewhere the user never asked to go, on a thread they were not
                // looking at. Resume instead.
                //
                // Both unknowns fail open. A thread id of 0 means the id could not be read rather
                // than that it differs, and discarding a real step on that basis would lose the
                // stop entirely — the target would run on to the next breakpoint, or forever.
                var stepping = _steppingThreadId;
                var completing = ThreadId(e.Thread);
                if (stepping != 0 && completing != 0 && completing != stepping)
                {
                    try { e.Controller.Continue(false); }
                    catch { /* already continued / terminated */ }
                    return;
                }

                // Landing in a DebuggerStepThrough method, a framework module, or code with no
                // symbols is not a stop the user asked for: step out and keep going, exactly as
                // Just My Code does in Visual Studio.
                if (_display.JustMyCode && !IsUserFrame(e.Thread) && TryStepOutOfNonUserCode(e.Thread))
                {
                    _stoppedThread = null;
                    try { e.Controller.Continue(false); }
                    catch { /* already continued / terminated */ }
                    return;
                }

                // Stepping out of somebody else's code lands back on the line the step started
                // from, which is not progress — it is the step appearing to do nothing. Re-arm the
                // original range so it carries on past the call instead of reporting a stop where
                // the user already was.
                if (TryResumeStepOverOrigin(e.Thread))
                {
                    _stoppedThread = null;
                    try { e.Controller.Continue(false); }
                    catch { /* already continued / terminated */ }
                    return;
                }

                // The step is over. Drop the steppers it armed so none of them can complete again.
                _stepOrigin = null;
                DeactivateSteppers();
                _stoppedOnException = false;
                _stoppedThread = e.Thread;
                Enqueue(FlushPendingDeltas);
                var (file, line, column) = ThreadLocation(e.Thread);
                Emit(DebugEventKind.Step, "step", MethodOf(e.Thread), ThreadId(e.Thread), file, line, column);
            };
            callback.OnException += (_, e) =>
            {
                // An exception raised by the code an evaluation is running belongs to the eval:
                // it completes as EvalException, and stopping on it here would leave the process
                // suspended with the eval's caller waiting on a completion that can never come.
                if (_pendingEval is not null)
                {
                    Emit(DebugEventKind.Diagnostic, $"exception during evaluation in {MethodOf(e.Thread)}", string.Empty, 0);
                    return;
                }

                // Whether this exception is one the user asked to see is decided here, on the
                // callback thread, while the process is already suspended. An exception the
                // policy rejects is reported and resumed without ever becoming a stop.
                var unhandled = e.Unhandled != 0;
                var thrown = Safe(() => e.Thread.CurrentException);
                var described = DescribeThrownException(thrown, unhandled);

                if (ShouldStopOnException(unhandled, thrown))
                {
                    // As on a breakpoint: the exception decided this stop, so an in-flight step is
                    // abandoned and its stepper must not survive to fire later.
                    DeactivateSteppers();
                    _stoppedOnException = true;
                    _stoppedThread = e.Thread;
                    Enqueue(FlushPendingDeltas);
                    var (file, line, column) = ThreadLocation(e.Thread);
                    Emit(
                        DebugEventKind.Exception,
                        described,
                        MethodOf(e.Thread), ThreadId(e.Thread), file, line, column);
                }
                else
                {
                    Emit(
                        DebugEventKind.Diagnostic,
                        $"{described} in {MethodOf(e.Thread)}",
                        string.Empty, 0);
                }
            };
            callback.OnExitProcess += (_, _) =>
            {
                Emit(DebugEventKind.Exited, DescribeExit(), string.Empty, 0);
                _exited.TrySetResult();
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
                // ... unless an eval is running: the stop context belongs to the breakpoint the
                // eval started from, and the exception must run on to complete the eval.
                if (e.Kind is CorDebugManagedCallbackKind.Exception && _stoppedThread is not null && _pendingEval is null)
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
                _watchdog.Starting("running a client command");
                try { command(); }
                catch (Exception ex) { Console.Error.WriteLine($"[debug] command failed: {ex.Message}"); }
                finally { _watchdog.Finished(); }
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

        // Deliberately not asking for process-wide EnC here.
        //
        // SetDesiredNGENCompilerFlags(CORDEBUG_JIT_ENABLE_ENC) does not add edit-and-continue to
        // the native images — it disqualifies them, so every framework assembly is rejected from
        // its NGen image and JITted from scratch, unoptimized, for the life of the process. It was
        // here to reach user code that loads from a native image, but user code is not NGen'd:
        // a web application's assemblies are compiled into the ASP.NET temporary files, and an
        // application's own build output is not run through ngen either. The modules it actually
        // changed were the framework's.
        //
        // Two things came of that. Startup on a large site paid to JIT the whole framework, and
        // unoptimized frames are much larger than optimized ones — deep enough recursion inside a
        // framework assembly then overflows a stack it fits in when run normally, which kills the
        // debuggee outright, with no exception anyone can catch or report.
        //
        // A user module that genuinely is NGen'd still gets its per-module attempt in
        // RegisterEncModule, and says so plainly when the runtime refuses.

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

    /// True for an App_Web_*.dll the ASP.NET page compiler generated. These hold nothing but
    /// markup-generated classes and inline &lt;% %&gt; code, so only a breakpoint in a markup
    /// file can ever bind in one — everything else the debugger would do per module (EnC
    /// flagging, PDB probes, load narration) is waste multiplied by dozens per page.
    internal static bool IsGeneratedWebFormsModule(string moduleName) =>
        Path.GetFileName(moduleName).StartsWith("App_Web_", StringComparison.OrdinalIgnoreCase);

    /// True when the breakpoint sits in a WebForms markup file — the one thing that binds into
    /// a generated App_Web_*.dll rather than the site's own assemblies.
    internal static bool TargetsMarkup(BreakpointSpec spec) =>
        Path.GetExtension(spec.FilePath).ToLowerInvariant()
            is ".aspx" or ".ascx" or ".master" or ".ashx" or ".asax" or ".asmx";

    /// JIT-flag a freshly loaded module for EnC (only valid during the LoadModule callback)
    /// and, for user modules, remember it by simple assembly name as an ApplyHotReload target.
    /// <remarks>
    /// Every module gets the flag attempt, not just the user's: an unflagged module anywhere in
    /// the process is a way for <c>ApplyChanges</c> to fault later, and the flag is only valid
    /// during the load callback, so there is no second chance to decide it was needed after all.
    /// NGen'd framework images refuse the flag; that refusal is expected and only
    /// narrated for modules the user could actually edit.
    /// </remarks>
    private void RegisterEncModule(CorDebugModule module, string moduleName, bool generated = false)
    {
        if (moduleName.Length == 0)
            return;
        bool user = IsUserModule(moduleName) && !generated;

        // Only the user's own modules are flagged, and the restriction is load-bearing.
        //
        // CORDEBUG_JIT_ENABLE_ENC is DISABLE_OPTIMIZATION with edit-and-continue on top, so every
        // module it succeeds on is JITted unoptimized for the life of the process. This used to be
        // attempted on all of them — the framework, the third-party stack, everything — defended
        // on the grounds that an unflagged module anywhere is a way for ApplyChanges to fault
        // later. It is not: ApplyChanges is only ever reached with a module out of _encModules,
        // and the registration below already puts nothing but the user's modules in there.
        //
        // What flagging everything actually bought was an unoptimized process. Unoptimized frames
        // are much larger than optimized ones, so recursion that fits its stack comfortably in a
        // normal run can overflow it under the debugger — and a StackOverflowException can be
        // neither caught nor reported, so the debuggee simply dies. A recursive directory walk
        // inside a framework assembly is deep enough to do it.
        if (!user)
            return;

        // Whether this succeeded decides whether a later delta can be applied at all: a module
        // JITted without the flag is not updatable, and ApplyChanges faults on it rather than
        // failing. Swallowing the result is how that stays invisible until the crash, so it is
        // recorded and only a flagged module is offered as a target.
        var flagged = HRESULT.E_FAIL;
        try { flagged = module.TrySetJITCompilerFlags(CorDebugJITCompilerFlags.CORDEBUG_JIT_ENABLE_ENC); }
        catch (Exception ex)
        {
            Emit(DebugEventKind.Diagnostic,
                $"EnC could not be enabled for {Path.GetFileName(moduleName)}: {ex.Message}",
                string.Empty, 0);
        }

        if (flagged != HRESULT.S_OK)
        {
            Emit(DebugEventKind.Diagnostic,
                $"EnC is unavailable for {Path.GetFileName(moduleName)} ({flagged}); " +
                "hot reload cannot change it.",
                string.Empty, 0);
            return;
        }

        var assemblyName = Path.GetFileNameWithoutExtension(moduleName);
        if (assemblyName.Length == 0)
            return;

        _encModules[assemblyName] = module;
        QueueHistoryReplay(module, assemblyName);
    }

    // --- Just My Code, as the runtime sees it ---------------------------------------------------

    /// <summary>
    /// The modules the runtime accepted as <em>the user's</em>, by module path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A set rather than a count, because the hazard it guards against is per-module. A JMC step
    /// runs until a method marked as the user's executes, so arming one in a frame whose own module
    /// was never accepted is not a filtered step — it is a continue. The thread runs on, no step
    /// complete is ever delivered, and the engine's own filtering never gets a chance to help
    /// because it only ever filters completes that arrive.
    /// </para>
    /// <para>
    /// "Somewhere in the process is marked" does not rule that out: a solution that builds one
    /// project in Debug and another optimized has exactly one of those modules in here, and a step
    /// taken in the other is the case that hangs.
    /// </para>
    /// </remarks>
    private readonly ConcurrentDictionary<string, byte> _jmcUserModules =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Tells the runtime whether a module is the user's, so its own Just My Code stepping has
    /// something to filter on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Marking is not advice the runtime can ignore or infer: an unmarked method is not the user's
    /// as far as a JMC step is concerned, so every module has to be marked one way or the other
    /// before any of it means anything.
    /// </para>
    /// <para>
    /// Optimized and NGen'd images refuse it — their methods are not debuggable in the sense JMC
    /// requires — and that refusal is expected rather than a failure. It is also why the engine's
    /// own filtering of step completes stays: the runtime cannot filter what it was not allowed to
    /// mark, and a step that lands in an optimized framework image still has to be stepped back out
    /// of by hand.
    /// </para>
    /// </remarks>
    private void MarkJustMyCode(CorDebugModule module, string moduleName)
    {
        if (moduleName.Length == 0)
            return;

        // Unknown is marked as the user's, matching what every other reading of it does: a module
        // nothing could classify is one a step should be willing to stop in.
        bool user = _userCode.Classify(moduleName) != UserCodeVerdict.External;

        HRESULT marked;
        try { marked = module.TrySetJMCStatus(user, 0, []); }
        catch { marked = HRESULT.E_FAIL; }

        // Only a clean acceptance counts. Anything else means at least some of the module's methods
        // were not marked, and this set answers "could a step armed here ever complete" — which a
        // partial answer does not settle.
        if (user && marked == HRESULT.S_OK)
            _jmcUserModules[moduleName] = 0;
        else
            _jmcUserModules.TryRemove(moduleName, out _);
    }

    /// <summary>
    /// Re-marks every loaded module after the answer to "is this the user's" has changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The solution arrives with the display options, which can be replaced mid-session — and by
    /// then most of the process is already loaded and marked against whatever was known before.
    /// The runtime keeps a marking until it is told otherwise, so without this a session that
    /// opened its solution late would step by the old answer for the rest of its life.
    /// </para>
    /// <para>
    /// Queued onto the session thread and taken through a stop, because the options are replaced by
    /// whichever thread the settings change arrived on and say nothing about whether the target is
    /// running. Enumerating a running process yields nothing at all — the failure is swallowed as a
    /// missing module list rather than raised — so doing this inline would quietly re-mark nothing
    /// while believing it had re-marked everything.
    /// </para>
    /// </remarks>
    private void RemarkLoadedModules()
    {
        if (_process is null)
            return;

        Enqueue(() => WhileSynchronized("apply the user-code classification", () =>
        {
            var seen = new List<(CorDebugModule Module, string Name)>();
            foreach (var module in LoadedModules())
            {
                if (Safe(() => module.Name) is { Length: > 0 } name)
                    seen.Add((module, name));
            }

            // Nothing enumerated is not the same as nothing loaded. Rather than clear what is known
            // and mark nothing in its place — which would leave the runtime holding the old answer
            // while this believed it held the new one — leave the previous marking standing.
            if (seen.Count == 0)
                return;

            _jmcUserModules.Clear();
            foreach (var (module, name) in seen)
                MarkJustMyCode(module, name);
        }));
    }

    /// <summary>
    /// Whether a step armed in this frame can be left to the runtime's own Just My Code.
    /// </summary>
    /// <remarks>
    /// Three things have to hold, and any one of them missing makes a JMC stepper worse than no
    /// stepper at all: the user wants the filtering, the solution said which assemblies are theirs
    /// (without it nothing is marked as the user's, and a filtered step never finds anywhere to
    /// stop), and <em>this frame's own module</em> was accepted — see <see cref="_jmcUserModules"/>
    /// for why the last one is not a question about the process as a whole.
    /// </remarks>
    private bool CanStepWithRuntimeJustMyCode(CorDebugFrame frame)
    {
        if (!_display.JustMyCode || !_userCode.KnowsTheSolution)
            return false;

        var moduleName = Safe(() => frame.Function?.Module?.Name);
        return moduleName is { Length: > 0 } && _jmcUserModules.ContainsKey(moduleName);
    }

    /// One-time "no symbols" diagnostic per user module, only when source breakpoints exist —
    /// the actionable cause of "my breakpoint never binds".
    private void ReportMissingSymbols(CorDebugModule module, string moduleName)
    {
        if (moduleName.Length == 0 || !IsUserModule(moduleName))
            return;
        // Excluded by the symbol globs is deliberate, not a missing PDB — reporting it would
        // tell the user to go fix a build that is fine.
        if (!SymbolGlobs.WantsSymbols(_display, moduleName))
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
            DebugEventKind.Diagnostic,
            $"no symbols for {Path.GetFileName(moduleName)} — source breakpoints in it cannot bind",
            string.Empty, 0);
    }

    /// Module unload (app-domain recycle, plugin unload): its bound breakpoints return to
    /// pending — the specs survive, so the next LoadModule rebinds them — and the client's
    /// gutter dots go hollow again via BreakpointUnbound events.
    /// <summary>
    /// Takes symbols the runtime delivered for a module and rebinds against them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fired for modules whose symbols exist only in the debuggee: <c>Reflection.Emit</c> output,
    /// generated serializer assemblies, in-memory view and Razor compilation. They have no PDB
    /// beside them on disk, so the ordinary open finds nothing and the module is reported as
    /// having no symbols — which, until this handler existed, was the end of it. A breakpoint in
    /// generated code could never bind, and a stop inside one showed a call stack with no source.
    /// </para>
    /// <para>
    /// The reader is replaced rather than added to. The runtime may send symbols more than once
    /// for the same module as more code is emitted into it, and the latest stream is the complete
    /// one — keeping the earlier reader would pin an older view of a module that has since grown.
    /// </para>
    /// </remarks>
    private void OnSymbolsUpdated(CorDebugModule module, ComStream stream)
    {
        var moduleName = Safe(() => module.Name) ?? string.Empty;
        if (moduleName.Length == 0)
            return;

        var metadata = Safe(() => Extensions.GetMetaDataInterface<MetaDataImport>(module));
        if (metadata is null)
            return;

        var pdb = Safe(() => ReadAll(stream));

        var opened = pdb is { Length: > 0 }
            ? SymbolReader.FromBytes(pdb, moduleName, metadata)
            : null;

        if (opened is not { } supplied)
        {
            Emit(
                DebugEventKind.Diagnostic,
                $"the runtime sent symbols for {Path.GetFileName(moduleName)} that could not be " +
                "read, so code generated into it stays without source",
                string.Empty, 0);
            return;
        }

        lock (_readers)
        {
            // Teardown has already closed the readers; opening another here would leak it and its
            // spilled file, since nothing runs after the drain to close them.
            if (_symbolsClosed)
            {
                supplied.Reader.Dispose();
                if (supplied.TempFile is { } orphan)
                    SymbolReader.TryDelete(orphan);
                return;
            }

            // Retired rather than disposed: the session thread may be reading through the reader
            // being replaced, and this runs on the runtime's callback thread.
            if (_readers.Remove(moduleName, out var previous) && previous is not null)
                _retiredReaders.Add(previous);
            _readers[moduleName] = supplied.Reader;
        }

        if (supplied.TempFile is { } spilled)
            _spilledSymbolFiles.Add(spilled);

        _symbolStatus[moduleName] = new SymbolStatusEntry(
            SymbolStatuses.Loaded, supplied.Reader.Origin, supplied.Reader.SymbolPath, string.Empty);

        // The module previously had no symbols, so it was reported as such and its breakpoints
        // were left pending. Both facts are now stale.
        lock (_noSymbolsReported)
            _noSymbolsReported.Remove(moduleName);

        Emit(
            DebugEventKind.Diagnostic,
            $"the runtime supplied symbols for {Path.GetFileName(moduleName)} at run time",
            string.Empty, 0);

        foreach (var spec in SpecsSnapshot())
            TryBindBreakpoint(module, spec);
    }

    /// <summary>
    /// Drains an <c>IStream</c> the runtime handed over into a byte array.
    /// </summary>
    /// <remarks>
    /// The COM stream reads into unmanaged memory and gives no managed overload, so a native
    /// buffer is unavoidable. It is also read from wherever the runtime left the pointer, so the
    /// seek to the start is what makes the result the whole PDB rather than its tail. The declared
    /// size is treated as a hint: reading until <c>Read</c> returns nothing is what actually
    /// decides the length, because a stream that reports more than it has would otherwise pad the
    /// PDB with whatever the buffer held.
    /// </remarks>
    private static byte[] ReadAll(ComStream stream)
    {
        stream.Seek(0L, STREAM_SEEK.STREAM_SEEK_SET);

        long declared = (long)stream.Stat(STATFLAG.STATFLAG_NONAME).cbSize;
        const int ChunkSize = 64 * 1024;
        int chunk = declared is > 0 and < ChunkSize ? (int)declared : ChunkSize;

        using var buffer = new MemoryStream(declared is > 0 and <= int.MaxValue ? (int)declared : 0);
        var native = Marshal.AllocHGlobal(chunk);
        try
        {
            var managed = new byte[chunk];
            while (true)
            {
                int read = stream.Read(native, chunk);
                if (read <= 0)
                    break;
                Marshal.Copy(native, managed, 0, read);
                buffer.Write(managed, 0, read);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(native);
        }

        return buffer.ToArray();
    }

    private void OnModuleUnloaded(CorDebugModule module, string moduleName)
    {
        var instanceKey = Safe(() => InstanceKey(module)) ?? string.Empty;
        var assemblyName = Path.GetFileNameWithoutExtension(moduleName);
        if (assemblyName.Length > 0)
            _encModules.TryRemove(assemblyName, out _);

        // The symbols went with it. Kept, they hold the PDB open for the rest of the session —
        // one handle per app-domain recycle, which a site rebuilt all afternoon does often — and
        // a module that reloads from the same path would be read through its predecessor's PDB.
        lock (_readers)
        {
            // Retired rather than disposed here too: this is the callback thread, and a stack walk
            // on the session thread can be part-way through this very reader.
            if (_readers.Remove(moduleName, out var unloaded) && unloaded is not null)
                _retiredReaders.Add(unloaded);
        }
        _symbolStatus.TryRemove(moduleName, out _);
        ForgetDecompiledSymbols(moduleName);
        lock (_noSymbolsReported)
            _noSymbolsReported.Remove(moduleName);

        // Only the placements that lived in this image. The same breakpoint can be armed in the
        // same assembly loaded into another app domain, and that one is still standing — reporting
        // the breakpoint as unbound would grey out a gutter marker that still stops the target.
        foreach (var (key, bound) in _bound.ToArray())
        {
            bool removed = instanceKey.Length > 0
                ? bound.RemoveInstance(instanceKey, out bool nowEmpty)
                : bound.RemoveModule(moduleName, out nowEmpty);
            if (!removed || !nowEmpty)
                continue;

            _bound.TryRemove(key, out _);
            _boundModule.TryRemove(key, out _);
            _boundSpecs.TryRemove(key, out var spec);
            // Source keys are "path|line"; entry keys have no separator (no gutter echo needed).
            var sep = key.LastIndexOf('|');
            if (sep > 0 && int.TryParse(key.AsSpan(sep + 1), out var line))
                Emit(
                    DebugEventKind.BreakpointUnbound,
                    $"module unloaded: {Path.GetFileName(moduleName)}",
                    string.Empty, 0, key[..sep], line,
                    breakpointId: spec?.Id ?? string.Empty);
        }
    }

    /// <summary>
    /// The success message for a delta that was accepted but not yet applied. The callers that
    /// relay outcomes across processes match on this prefix, so it is part of the wire contract.
    /// </summary>
    public const string DeltaQueuedPrefix = "queued: ";

    /// <summary>Deltas accepted while no safe stop existed, applied in order at the next real
    /// debug-event stop. Session thread only.</summary>
    private readonly Queue<PendingDelta> _pendingDeltas = new();

    /// <summary>One edit still waiting for a stop it can safely be applied from.</summary>
    /// <param name="Sequence">Its position in this session's edit history, which is also what
    /// keeps a symbol reader from being told about the same edit twice.</param>
    /// <param name="Target">The one module instance to apply to, when this is a replay into a
    /// freshly loaded module. Null means the ordinary case: every loaded instance of the
    /// assembly.</param>
    private sealed record PendingDelta(
        long Sequence,
        string AssemblyName,
        byte[] Metadata,
        byte[] Il,
        byte[] Pdb,
        string? Map,
        CorDebugModule? Target = null);

    /// <summary>
    /// Every edit applied in this session, in order, keyed by the build it was computed against.
    /// </summary>
    /// <remarks>
    /// A hosted app recycles its app domain and loads the same assemblies again from scratch, at
    /// which point the running code is the code that was built, not the code the user has been
    /// editing for the last half hour. Keyed by MVID so a rebuild — a genuinely different image
    /// that happens to share the name — is never handed edits computed against the old one.
    /// </remarks>
    private readonly ConcurrentDictionary<Guid, List<PendingDelta>> _deltaHistory = new();

    /// <summary>Replays a module load asked for, waiting to be moved onto the session thread.
    /// Separate because <see cref="_pendingDeltas"/> belongs to the session thread and module
    /// loads arrive on the runtime's callback thread.</summary>
    private readonly ConcurrentQueue<PendingDelta> _replayDeltas = new();

    private long _deltaSequence;

    /// Apply one EnC metadata+IL delta to a live module (by simple assembly name), marshalled
    /// onto the session thread. Applied immediately from a safe break state, queued otherwise.
    public Task<(bool Ok, string Error)> ApplyDeltaAsync(
        string assemblyName, byte[] metadata, byte[] il, byte[] pdb, string? symbolMap = null)
        => InvokeAsync<(bool Ok, string Error)>(() =>
    {
        if (_encPoisoned)
            return (false, "a previous edit failed to apply; this session can no longer be edited");
        if (!_encModules.TryGetValue(assemblyName, out var module))
            return (false, $"module '{assemblyName}' is not loaded in the debuggee");
        var process = _process;
        if (process is null)
            return (false, "no process");

        // Before anything else, so a module that reloaded is caught up on the edits it missed
        // before this one is offered to it. A delta computed against a later generation cannot be
        // applied to a module still on an earlier one, and the queue is what keeps them in order.
        DrainReplays();

        // A stop alone is not enough. What ApplyChanges tolerates was mapped empirically against
        // IIS Express: from a break state whose adopted thread sits in user code of the edited
        // module's app domain it succeeds — breakpoint stop or Break All alike — while the same
        // call from a Break All into an idle server, where the only adoptable threads are
        // framework threads in other domains, access-violates with no HRESULT and no managed
        // exception. An unsafe stop is therefore not applied from; the delta is queued and lands
        // at the next real debug-event stop, which is the state that is known to survive. Edits
        // stay ordered: once one delta waits, later ones wait behind it.
        if (_stoppedThread is not null && _pendingDeltas.Count > 0)
            FlushPendingDeltas();
        if (_encPoisoned)
            return (false, "a previous edit failed to apply; this session can no longer be edited");

        if (_stoppedThread is null || _pendingDeltas.Count > 0 ||
            (!StoppedThreadIsUserCodeIn(module) && !UserCodeIsStoppedIn(module)))
        {
            _pendingDeltas.Enqueue(new PendingDelta(
                NextDeltaSequence(), assemblyName, metadata, il, pdb, symbolMap));
            return (true, DeltaQueuedPrefix +
                "no user code is stopped in the edited module's app domain, so the edit is " +
                "queued and will be applied at the next breakpoint hit in the app's own code");
        }

        return ApplyDeltaCore(
            module,
            new PendingDelta(NextDeltaSequence(), assemblyName, metadata, il, pdb, symbolMap));
    });

    private long NextDeltaSequence() => Interlocked.Increment(ref _deltaSequence);

    /// <summary>
    /// Moves replays the callback thread asked for onto the session thread's own queue.
    /// </summary>
    /// <remarks>
    /// Ordered by the edit each one is, not by when it was queued. A delta is only valid against the
    /// generation immediately before it, and a replay is by definition an older edit than anything
    /// still waiting — so an edit queued while the app was running, followed by a recycle, has to
    /// let the reloaded module catch up first or it is handed a delta several generations ahead of
    /// the baseline it actually has.
    /// </remarks>
    private void DrainReplays()
    {
        if (_replayDeltas.IsEmpty)
            return;

        while (_replayDeltas.TryDequeue(out var replay))
            _pendingDeltas.Enqueue(replay);

        var ordered = _pendingDeltas.OrderBy(d => d.Sequence).ToArray();
        _pendingDeltas.Clear();
        foreach (var delta in ordered)
            _pendingDeltas.Enqueue(delta);
    }

    /// <summary>The apply itself plus everything the runtime does not do for the debugger:
    /// symbol store update, breakpoint invalidation and rebind. Session thread, stopped
    /// process, safe stop shape — the callers own those preconditions.</summary>
    /// <remarks>
    /// Applied to every loaded instance of the module, not just the registered one: a hosted
    /// app can load the same assembly into more than one app domain (matched by MVID so a
    /// different build of the same name is never touched), and an instance left on the old
    /// code would silently diverge from the one the user watched update. A replay is the exception:
    /// it exists precisely because one instance is behind the others, so it goes to that one alone.
    /// </remarks>
    private (bool Ok, string Error) ApplyDeltaCore(CorDebugModule module, PendingDelta delta)
    {
        var (_, assemblyName, metadata, il, pdb, symbolMap, target) = delta;

        try
        {
            List<CorDebugModule> instances =
                target is not null ? [target] : InstancesOf(module, assemblyName);
            var metaPtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(metadata.Length);
            var ilPtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(il.Length);
            try
            {
                System.Runtime.InteropServices.Marshal.Copy(metadata, 0, metaPtr, metadata.Length);
                System.Runtime.InteropServices.Marshal.Copy(il, 0, ilPtr, il.Length);
                foreach (var instance in instances)
                {
                    var hr = instance.TryApplyChanges(metadata.Length, metaPtr, il.Length, ilPtr);
                    if (hr != HRESULT.S_OK)
                    {
                        // A half-applied edit leaves the runtime's metadata and the debugger's
                        // view disagreeing, and there is no way to roll it back. Further edits
                        // would build on that, so the session stops accepting them.
                        _encPoisoned = true;
                        return (false, $"ApplyChanges failed: {hr}");
                    }
                }
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(metaPtr);
                System.Runtime.InteropServices.Marshal.FreeHGlobal(ilPtr);
            }

            if (target is null && instances.Count > 1)
            {
                Emit(DebugEventKind.Diagnostic,
                    $"hot reload: {assemblyName} is loaded {instances.Count} times; " +
                    "the edit was applied to every instance", string.Empty, 0);
            }

            // Only edits that reached the runtime are worth replaying into a module that reloads
            // later, and only once — a replay is already in the history it came from.
            if (target is null)
                RememberDelta(module, delta);

            // Exactly the instances the runtime delta reached, and no others. An instance
            // InstancesOf declined to update keeps the code it is running, so its symbols have to
            // keep describing that code — and matching readers by name alone would hand this delta
            // to the reader of a different build that happens to share the assembly name.
            RefreshSymbolsAfterEdit(
                assemblyName, pdb, EncSymbolMap.Parse(symbolMap), delta.Sequence, instances);
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Files an applied edit under the build it was computed against, so a module that loads later
    /// can be brought up to the same generation.
    /// </summary>
    /// <remarks>
    /// A module with no MVID to read — dynamic, or an image the debugger cannot open — is not
    /// remembered: there would be no way to tell later whether a reloaded module is the same build,
    /// and replaying an edit into the wrong one corrupts it.
    /// </remarks>
    private void RememberDelta(CorDebugModule module, PendingDelta delta)
    {
        if (MvidOf(module) is not { } mvid)
            return;

        var history = _deltaHistory.GetOrAdd(mvid, _ => []);
        lock (history)
        {
            if (!history.Any(d => d.Sequence == delta.Sequence))
                history.Add(delta);
        }
    }

    /// <summary>
    /// Queues this session's edit history into a module that has just loaded, when it is the same
    /// build those edits were computed against.
    /// </summary>
    /// <remarks>
    /// This is what makes hot reload survive an application recycle: the host tears the app domain
    /// down and loads the assemblies again from their built images, and without this the running
    /// code silently reverts to the last build while the editor still shows the edits. Queued
    /// rather than applied here, because this runs on the runtime's callback thread during a module
    /// load — the stop shape ApplyChanges needs is the one the pending-delta queue already waits
    /// for.
    /// </remarks>
    private void QueueHistoryReplay(CorDebugModule module, string assemblyName)
    {
        if (_encPoisoned || _deltaHistory.IsEmpty)
            return;
        if (MvidOf(module) is not { } mvid || !_deltaHistory.TryGetValue(mvid, out var history))
            return;

        PendingDelta[] replays;
        lock (history)
            replays = [.. history];

        if (replays.Length == 0)
            return;

        foreach (var delta in replays)
            _replayDeltas.Enqueue(delta with { Target = module });

        Emit(DebugEventKind.Diagnostic,
            $"hot reload: {assemblyName} was loaded again; its {replays.Length} applied edit(s) " +
            "will be re-applied at the next breakpoint hit in the app's own code",
            string.Empty, 0);
    }

    /// <summary>
    /// Every loaded instance of the assembly the edit targets, starting with the registered
    /// one. Same simple name and same MVID — never a different build that shares the name.
    /// </summary>
    /// <remarks>
    /// An instance in another app domain is included only when that domain also has a safe
    /// stop shape: the empirical ApplyChanges rule is per-domain, and faulting the runtime to
    /// update a secondary instance is a worse outcome than leaving it briefly stale.
    /// </remarks>
    private List<CorDebugModule> InstancesOf(CorDebugModule primary, string assemblyName)
    {
        var instances = new List<CorDebugModule> { primary };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { InstanceKey(primary) };
        var mvid = MvidOf(primary);
        var primaryDomain = Safe(() => primary.Assembly.AppDomain)?.Id;

        foreach (var candidate in LoadedModules())
        {
            var name = Safe(() => candidate.Name) ?? string.Empty;
            if (!string.Equals(
                    Path.GetFileNameWithoutExtension(name), assemblyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!seen.Add(InstanceKey(candidate)))
                continue;
            if (mvid is { } expected && MvidOf(candidate) is { } actual && actual != expected)
                continue;

            var domain = Safe(() => candidate.Assembly.AppDomain)?.Id;
            if (domain != primaryDomain &&
                !StoppedThreadIsUserCodeIn(candidate) && !UserCodeIsStoppedIn(candidate))
            {
                Emit(DebugEventKind.Diagnostic,
                    $"hot reload: an instance of {assemblyName} in another app domain has no stopped " +
                    "user-code thread and keeps the old code for now", string.Empty, 0);
                continue;
            }

            instances.Add(candidate);
        }

        return instances;
    }

    /// <summary>One loaded module instance: the same image can appear once per app domain.</summary>
    private static string InstanceKey(CorDebugModule module)
    {
        var domain = Safe(() => module.Assembly.AppDomain)?.Id ?? -1;
        return $"{domain}|{Safe(() => module.Name) ?? module.GetHashCode().ToString()}";
    }

    /// <summary>
    /// Where a remapped frame should resume in the new version of an edited method: the start
    /// of the sequence point containing the old IL offset in the new version's map. The symbol
    /// reader was updated with the delta PDB when the edit applied, so its map is the new one.
    /// Falls back to the old offset itself; the runtime rejects an invalid target and the frame
    /// then finishes on the old version, which is the pre-remap behaviour.
    /// </summary>
    private int RemapOffsetFor(CorDebugFunction newFunction, int oldOffset)
    {
        try
        {
            var moduleName = Safe(() => newFunction.Module.Name) ?? string.Empty;
            if (moduleName.Length > 0 &&
                ReaderFor(newFunction.Module, moduleName) is { } reader &&
                SequencePointAtOffset(reader, newFunction.Token, oldOffset) is { } match)
            {
                return match.Offset;
            }
        }
        catch
        {
        }
        return oldOffset;
    }

    /// <summary>The module's MVID, or null when it cannot be read.</summary>
    /// <remarks>
    /// From the image file, not from the live metadata interface: ClrDebug 0.4.1's
    /// <c>GetScopeProps</c> wrapper access-violates in its GUID marshaller, and an
    /// <see cref="AccessViolationException"/> is uncatchable — it took the whole worker down.
    /// A module with no file behind it (dynamic, in-memory) answers null and is then matched
    /// by name alone.
    /// </remarks>
    private static Guid? MvidOf(CorDebugModule module)
    {
        try
        {
            var path = Safe(() => module.Name);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;
            using var stream = File.OpenRead(path);
            using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
            var metadata = pe.GetMetadataReader();
            return metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Applies whatever edits were queued while no safe stop existed. The managed callbacks
    /// that report real debug-event stops enqueue this onto the session thread right after
    /// recording the stop context, so it runs ahead of any continue or step the user can issue
    /// — by the time execution resumes, the code they edited is the code that runs.
    /// </summary>
    private void FlushPendingDeltas()
    {
        DrainReplays();
        if (_pendingDeltas.Count == 0)
            return;
        if (_stoppedThread is null)
        {
            Emit(DebugEventKind.Diagnostic,
                "hot reload: the stop ended before the queued edit could be applied; it stays queued",
                string.Empty, 0);
            return;
        }

        while (_pendingDeltas.Count > 0)
        {
            var pending = _pendingDeltas.Peek();
            string assemblyName = pending.AssemblyName;

            if (_encPoisoned)
            {
                _pendingDeltas.Clear();
                return;
            }

            // A replay names the one instance it is catching up, and that instance can itself have
            // gone away — a host that recycled once can recycle again before the next stop. Its
            // edits are still in the history, so the new instance will be offered them in turn;
            // this one is dropped rather than applied to a module that no longer exists.
            if (pending.Target is { } replayTarget && !StillLoaded(replayTarget))
            {
                _pendingDeltas.Dequeue();
                continue;
            }

            // Not loaded (an app-domain recycle unloaded it) or still no safe context here:
            // keep the queue for a later stop rather than dropping the user's edit.
            var module = pending.Target;
            if (module is null && !_encModules.TryGetValue(assemblyName, out module))
            {
                Emit(DebugEventKind.Diagnostic,
                    $"hot reload: '{assemblyName}' is not loaded right now; its queued edit waits for a later stop",
                    string.Empty, 0);
                return;
            }
            // The callback that announced this stop may still be unwinding on the runtime's
            // callback thread, and until it returns, inspecting the stopped thread from here
            // can transiently fail — which must read as "not yet", not "unsafe", or the edit
            // misses the exact stop it was queued for. Hence the brief retry.
            var safe = false;
            for (int attempt = 0; attempt < 40 && _stoppedThread is not null; attempt++)
            {
                safe = StoppedThreadIsUserCodeIn(module) || UserCodeIsStoppedIn(module);
                if (safe)
                    break;
                Thread.Sleep(25);
            }
            if (!safe)
            {
                Emit(DebugEventKind.Diagnostic,
                    $"hot reload: this stop has no user-code thread in {assemblyName}'s app domain; " +
                    $"its queued edit waits for a later stop [{DescribeStopForEnc(module)}]", string.Empty, 0);
                return;
            }

            var (ok, error) = ApplyDeltaCore(module, pending);
            _pendingDeltas.Dequeue();

            Emit(DebugEventKind.Diagnostic,
                ok
                    ? $"hot reload: the queued edit was applied to {assemblyName}"
                    : $"hot reload: the queued edit for {assemblyName} failed: {error}",
                string.Empty, 0);

            if (!ok)
            {
                // Everything behind the failed edit is dropped, and those edits were reported to
                // the user as accepted — the compiler's baseline has already moved past them. Every
                // later edit would be computed against generations the debuggee never received, so
                // the session stops accepting them rather than building on a fiction.
                _pendingDeltas.Clear();
                _encPoisoned = true;
                Emit(DebugEventKind.Diagnostic,
                    "hot reload: the edits still queued behind it were dropped; this session can no " +
                    "longer be edited, so restart it to pick the changes back up",
                    string.Empty, 0);
                return;
            }
        }
    }

    /// <summary>Whether a module the session held on to is still one the process has loaded.</summary>
    private bool StillLoaded(CorDebugModule module)
    {
        var key = Safe(() => InstanceKey(module));
        if (key is null)
            return false;

        foreach (var candidate in LoadedModules())
        {
            if (string.Equals(Safe(() => InstanceKey(candidate)), key, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the thread a breakpoint event names is actually sitting at that breakpoint —
    /// its active frame is the breakpoint's own function. False means the event is stale: the
    /// thread ran on before the callback was delivered, and there is no stop context to show.
    /// Unverifiable states count as genuine, so a real stop is never dropped.
    /// </summary>
    private static bool ThreadIsAtBreakpoint(CorDebugThread thread, CorDebugBreakpoint? breakpoint)
    {
        try
        {
            if (breakpoint is not CorDebugFunctionBreakpoint functionBreakpoint)
                return true;
            // A genuine hit always has the breakpoint's own function as the active frame. No
            // active frame means the thread is not even in managed code any more — the second
            // shape the stale delivery takes (the first shows a thread-pool frame instead).
            var frame = thread.ActiveFrame;
            if (frame is null)
                return false;
            if (frame.Function is not { } function)
                return true;
            return function.Token == functionBreakpoint.Function.Token &&
                string.Equals(
                    Safe(() => function.Module.Name), Safe(() => functionBreakpoint.Function.Module.Name),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Whether the stopped thread itself sits in user code of the module's own app domain —
    /// judged from metadata alone. The symbol-reader stack walk below can fail transiently at
    /// the instant a breakpoint callback lands (observed against IIS Express: the leaf frame
    /// resolves by metadata while its source mapping comes back empty), and a queued edit must
    /// not miss its stop because of that.
    /// </summary>
    /// <summary>Debug-detail for the refusal diagnostic: what the stopped thread looked like.</summary>
    private string DescribeStopForEnc(CorDebugModule module)
    {
        try
        {
            var thread = _stoppedThread;
            if (thread is null)
                return "no stopped thread";
            var frame = thread.ActiveFrame;
            if (frame is null)
                return "no active frame";
            var function = frame.Function;
            var name = Safe(() => function.Module.Name) ?? "?";
            var frameDomain = Safe(() => function.Module.Assembly.AppDomain);
            var moduleDomain = Safe(() => module.Assembly.AppDomain);
            return $"leaf tok={(uint)function.Token:X} mod={Path.GetFileName(name)} " +
                $"frameDom={(frameDomain is null ? "?" : frameDomain.Id.ToString())} " +
                $"editDom={(moduleDomain is null ? "?" : moduleDomain.Id.ToString())} user={IsUserModule(name)}";
        }
        catch (Exception ex)
        {
            return $"inspect failed: {ex.Message}";
        }
    }

    private bool StoppedThreadIsUserCodeIn(CorDebugModule module)
    {
        try
        {
            var frame = _stoppedThread?.ActiveFrame;
            if (frame?.Function is not { } function)
                return false;
            return function.Module.Assembly.AppDomain.Id == module.Assembly.AppDomain.Id &&
                IsUserModule(Safe(() => function.Module.Name) ?? string.Empty);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Whether some thread in the module's own app domain is stopped with user code on its
    /// stack — the stop shape ApplyChanges is known to survive.
    /// </summary>
    /// <remarks>
    /// Anywhere on the stack, not just the active frame: a thread parked in
    /// <c>Thread.Sleep</c> inside the user's own loop is exactly the case that works, and its
    /// active frame is framework code. The walk is bounded per thread because a stop context is
    /// being classified, not a stack reported.
    /// </remarks>
    private bool UserCodeIsStoppedIn(CorDebugModule module)
    {
        var domain = Safe(() => module.Assembly.AppDomain);
        if (domain is null)
            return false;

        foreach (var thread in Safe(() => domain.Threads) ?? Array.Empty<CorDebugThread>())
        {
            int depth = 0;
            foreach (var chain in Safe(() => thread.Chains) ?? Array.Empty<CorDebugChain>())
            {
                foreach (var frame in Safe(() => chain.Frames) ?? Array.Empty<CorDebugFrame>())
                {
                    if (FrameLocation(frame).File.Length > 0)
                        return true;
                    if (++depth >= 64)
                        break;
                }
                if (depth >= 64)
                    break;
            }
        }

        return false;
    }

    /// <summary>
    /// Brings the debugger's own view back in line with the edit the runtime just took.
    /// </summary>
    /// <remarks>
    /// <c>ApplyChanges</c> updates the runtime and nothing else. Line numbers, sequence points and
    /// local scopes for the edited method live in the debugger's symbol reader, which still holds
    /// the pre-edit PDB — so without this every breakpoint and every reported location in that
    /// method silently points at the old source. The unmanaged reader takes the delta directly;
    /// a portable reader has no equivalent, so its cache is dropped instead and the stale entry is
    /// at least not kept.
    /// </remarks>
    /// <param name="map">How the edit moved the lines of methods the delta does not describe.
    /// Without it only the changed methods come out right and everything below them in the same
    /// file drifts; see <see cref="EncSymbolMap"/>.</param>
    /// <param name="sequence">Which edit this is, so a reader is never told about the same one
    /// twice — the replay into a reloaded module walks over edits some readers already have.</param>
    /// <param name="instances">The module instances the runtime delta actually reached. Exactly
    /// these, because a reader describes the code one image is running: an instance that was left
    /// on the old code has to keep the old line numbers, and an instance behind on generations has
    /// to be caught up one edit at a time.</param>
    private void RefreshSymbolsAfterEdit(
        string assemblyName, byte[] pdb, EncSymbolMap? map, long sequence,
        IReadOnlyList<CorDebugModule> instances)
    {
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var instance in instances)
        {
            string path = Safe(() => instance.Name) ?? string.Empty;
            // Two app domains sharing one image share one reader, and telling it twice about the
            // same edit would move its lines twice as far as the edit did.
            if (path.Length == 0 || !seenPaths.Add(path))
                continue;

            // Opened here rather than looked up. A recycled app domain shadow-copies the assembly
            // to a path nothing has read symbols for yet, and letting the first breakpoint bind
            // create that reader afterwards would start it at the built code — with every replayed
            // edit missing and no sign that anything was wrong.
            var reader = ReaderFor(instance, path);
            if (reader is null)
                continue;

            // Already told about this edit; see the replay path.
            if (reader.AppliedEdits.Contains(sequence))
                continue;

            bool updated = false;
            if (reader.Unmanaged is { } unmanaged && pdb.Length > 0)
            {
                try
                {
                    updated = UpdateSymbolStoreAfterEdit(unmanaged, pdb, map);
                }
                catch (Exception ex)
                {
                    Emit(DebugEventKind.Diagnostic,
                        $"the symbol store could not be updated after the edit: {ex.Message}",
                        string.Empty, 0);
                }
            }

            // Recorded only once it worked, so a reader that has to be rebuilt is offered the edit
            // again rather than being treated as though it had taken it.
            if (updated)
            {
                reader.AppliedEdits.Add(sequence);
                continue;
            }

            lock (_readers)
                _readers.Remove(path);
            _retiredReaders.Add(reader);
            // The decompilation was of the pre-edit IL, so it is stale for the same reason and by
            // the same amount. Left standing it would become this module's symbols the moment the
            // PDB reader is gone, which is the opposite of what dropping the reader was for.
            ForgetDecompiledSymbols(path);
            Emit(DebugEventKind.Diagnostic,
                $"line information for {assemblyName} is stale after the edit; " +
                "breakpoints in changed methods may bind to the wrong line.",
                string.Empty, 0);
        }

        // A method token alone no longer identifies code: the edited method has a new version, and
        // bindings made against the old one would resolve to it. Dropping them returns the
        // affected breakpoints to pending, and the specs survive, so they rebind.
        var dropped = false;
        foreach (var (key, bound) in _bound.ToArray())
        {
            // Every placement, in every app domain: the edit went to all of them, so a binding
            // anywhere in this assembly names a method version that no longer exists.
            if (!bound.InAssembly(assemblyName))
                continue;

            _boundModule.TryRemove(key, out _);
            // Dropping the reference without deactivating leaks a live native breakpoint into the
            // runtime, and a leaked breakpoint is what later makes detach fail.
            if (_bound.TryRemove(key, out _))
                bound.DeactivateAll();
            _boundSpecs.TryRemove(key, out var unbound);
            dropped = true;

            // The rebind below usually puts these straight back, but it is allowed to fail — that
            // is what the stale-symbols warning above is about. Announcing the unbind means a
            // breakpoint that does not come back is drawn as pending rather than left looking
            // armed, which is the one state the client cannot recover on its own.
            var sep = key.LastIndexOf('|');
            if (sep > 0 && int.TryParse(key.AsSpan(sep + 1), out var line))
            {
                Emit(
                    DebugEventKind.BreakpointUnbound,
                    $"rebinding after an edit to {assemblyName}",
                    string.Empty, 0, key[..sep], line,
                    breakpointId: unbound?.Id ?? string.Empty);
            }
        }

        // Nothing else would rebind them: the edited module does not raise LoadModule again. This
        // runs on the session thread with the process stopped, which is what binding requires.
        if (!dropped)
            return;
        foreach (var module in LoadedModules())
            foreach (var spec in SpecsSnapshot())
                TryBindBreakpoint(module, spec);
    }

    /// <summary>
    /// Hands one unmanaged reader the delta PDB, together with how far every method the delta does
    /// not describe has moved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The plain update takes the delta and nothing else, which is enough for the methods that were
    /// edited and wrong for every method below them in the same file: those still carry the line
    /// numbers they had before the edit inserted or removed lines above them, and the error grows
    /// with each further edit. The Edit-and-Continue entry point takes the same delta plus a list of
    /// "this method moved by N lines", which is exactly what the compiler's own line map says.
    /// </para>
    /// <para>
    /// Falls back to the plain update when the reader does not offer the richer one, so a reader
    /// implementation without it still gets the edited methods right rather than nothing.
    /// </para>
    /// </remarks>
    private static bool UpdateSymbolStoreAfterEdit(
        SymUnmanagedReader unmanaged, byte[] pdb, EncSymbolMap? map)
    {
        if (unmanaged.Raw is ISymUnmanagedENCUpdate enc)
        {
            // Computed before the update, because it asks the reader where the methods used to be.
            var moved = MovedMethods(unmanaged, map);

            // The native side reads `count` entries from the pointer; one spare element only exists
            // so there is something to take a reference to when nothing moved.
            var buffer = new SYMLINEDELTA[Math.Max(1, moved.Count)];
            moved.CopyTo(buffer);

            var hr = enc.UpdateSymbolStore2(new ByteArrayStream(pdb), ref buffer[0], moved.Count);
            if (hr == HRESULT.S_OK)
                return true;

            // Only a reader that never started is offered the delta a second time. Any other
            // failure may have taken part of it, and handing the same delta over again would then
            // apply half of it twice — the fallback is for readers without this entry point, not a
            // retry.
            if (hr != HRESULT.E_NOTIMPL && hr != HRESULT.E_NOINTERFACE)
                return false;
        }

        // By file rather than by stream: this reader accepts a path, and the fallback is not the
        // place to introduce a second way of failing.
        string temporary = Path.Combine(Path.GetTempPath(), $"roslyn-sense-enc-{Guid.NewGuid():N}.pdb");
        try
        {
            File.WriteAllBytes(temporary, pdb);
            return unmanaged.TryUpdateSymbolStore(temporary, null) == HRESULT.S_OK;
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
    }

    /// <summary>
    /// Every method in the edited files that the delta does not describe, paired with how many
    /// lines it moved.
    /// </summary>
    /// <remarks>
    /// The compiler reports line movements per file as runs — "from this line on, add N" — while
    /// the symbol store wants them per method, so each method is placed by the line it starts on.
    /// Methods the delta already describes are skipped: their new lines are in the delta PDB, and
    /// shifting them again would move them twice.
    /// </remarks>
    private static List<SYMLINEDELTA> MovedMethods(SymUnmanagedReader reader, EncSymbolMap? map)
    {
        var moved = new List<SYMLINEDELTA>();
        if (map is null)
            return moved;

        var edited = new HashSet<int>(map.UpdatedMethods);

        foreach (var file in map.Files)
        {
            if (file.Shifts.Length == 0)
                continue;

            if (DocumentFor(reader, file.File) is not { } document)
                continue;
            if (reader.TryGetMethodsInDocument(document.Raw, out var methods) != HRESULT.S_OK)
                continue;

            foreach (var method in methods ?? [])
            {
                if (method is null)
                    continue;

                int token = method.Token;
                if (edited.Contains(token))
                    continue;

                int line = FirstLineOf(method);
                if (line <= 0)
                    continue;

                // Symbol lines count from 1, the compiler's line map from 0.
                int delta = file.ShiftAt(line - 1);
                if (delta != 0)
                    moved.Add(new SYMLINEDELTA { mdMethod = token, delta = delta });
            }
        }

        return moved;
    }

    /// <summary>
    /// The reader's document for a compiler-reported path.
    /// </summary>
    /// <remarks>
    /// Asked for by path first, since that is what a PDB stores. The scan by file name behind it
    /// covers the case where the two spell the same file differently — a deterministic build with
    /// mapped source roots being the usual reason — because giving up there would silently mean
    /// "nothing in this file moved".
    /// </remarks>
    private static SymUnmanagedDocument? DocumentFor(SymUnmanagedReader reader, string path)
    {
        if (reader.TryGetDocument(path, Guid.Empty, Guid.Empty, Guid.Empty, out var document) == HRESULT.S_OK &&
            document is not null)
        {
            return document;
        }

        string name = Path.GetFileName(path);
        if (name.Length == 0)
            return null;

        if (reader.TryGetDocuments(out var documents) != HRESULT.S_OK)
            return null;

        SymUnmanagedDocument? match = null;
        foreach (var candidate in documents ?? [])
        {
            string url = Safe(() => candidate?.URL) ?? string.Empty;
            if (!string.Equals(Path.GetFileName(url), name, StringComparison.OrdinalIgnoreCase))
                continue;

            // Two files with the same name in different folders cannot be told apart from a name,
            // and shifting the wrong one is worse than shifting neither.
            if (match is not null)
                return null;
            match = candidate;
        }

        return match;
    }

    /// <summary>The first line the method has code on, or 0 when its symbols say nothing.</summary>
    /// <remarks>
    /// Sequence points are not ordered by line — a compiler-generated state machine reorders them
    /// freely — so this is the minimum rather than the first. The hidden marker is excluded because
    /// it is a sentinel line number, not a place in the file.
    /// </remarks>
    private static int FirstLineOf(SymUnmanagedMethod method)
    {
        const int HiddenLine = 0xFEEFEE;

        int count = Safe(() => (int?)method.SequencePointCount) ?? 0;
        if (count <= 0)
            return 0;

        if (Safe(() => method.GetSequencePoints(count).lines) is not { Length: > 0 } lines)
            return 0;

        int first = 0;
        foreach (int line in lines)
        {
            if (line <= 0 || line >= HiddenLine)
                continue;
            if (first == 0 || line < first)
                first = line;
        }

        return first;
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
        string FilePath,
        /// <summary>Where the next sequence point starts, so a source-level step knows how much IL
        /// this line covers. <c>0</c> when the caller resolved a point rather than located one.</summary>
        int NextOffset = 0);

    private readonly record struct ResolvedSequencePoint(mdMethodDef MethodToken, SequencePointMatch Match);

    /// <summary>One source document a PDB describes, from either reader.</summary>
    /// <param name="Url">The path as the PDB spells it, which is the build machine's path and not
    /// necessarily one that exists here.</param>
    /// <param name="ChecksumAlgorithm">Which hash <paramref name="Checksum"/> is; empty when the
    /// PDB did not say.</param>
    /// <param name="Checksum">The hash of the source the build compiled — the only evidence that a
    /// local file with a different path is the same file.</param>
    private readonly record struct SymbolDocument(
        string Url,
        SymUnmanagedDocument? Unmanaged,
        DocumentHandle Portable,
        Guid ChecksumAlgorithm = default,
        byte[]? Checksum = null);

    /// <summary>What was found when a module's symbols were looked for, for reporting.</summary>
    /// <param name="Status">One of <see cref="SymbolStatuses"/>.</param>
    /// <param name="Origin">One of <see cref="SymbolOrigins"/>; empty unless symbols loaded.</param>
    /// <param name="Path">The file the symbols were read from; empty when they were never a file.</param>
    /// <param name="Detail">The reason behind <paramref name="Status"/>, in a sentence.</param>
    private readonly record struct SymbolStatusEntry(
        string Status, string Origin, string Path, string Detail);

    /// <summary>
    /// A portable PDB opened for a module, located the way the runtime itself would locate it.
    /// </summary>
    /// <remarks>
    /// Deliberately not "the .pdb next to the .dll". That guess misses three cases that matter: a
    /// PDB embedded in the assembly, a PDB the compiler recorded at a different path in the debug
    /// directory, and a precompiled image named <c>Foo.ni.dll</c>, whose sibling would be computed
    /// as the never-present <c>Foo.ni.pdb</c>. It is also unverified — any stale PDB left beside a
    /// rebuilt assembly is accepted, which binds breakpoints to lines that have since moved.
    /// <see cref="PEReader.TryOpenAssociatedPortablePdb"/> reads the debug directory, handles the
    /// embedded case, and checks the PDB's id against the one the assembly was built with, so all
    /// four problems are answered by using it instead.
    /// </remarks>
    private sealed class PortablePdbReader : IDisposable
    {
        private readonly PEReader? _peReader;
        private readonly MetadataReaderProvider _provider;

        private PortablePdbReader(PEReader? peReader, MetadataReaderProvider provider, string path)
        {
            _peReader = peReader;
            _provider = provider;
            Path = path;
            Reader = provider.GetMetadataReader();
        }

        public MetadataReader Reader { get; }

        /// <summary>Where the symbols came from, for diagnostics.</summary>
        public string Path { get; }

        /// <summary>A reader over portable-PDB bytes already in hand, rather than a file.</summary>
        public static PortablePdbReader? FromBytes(byte[] pdb, string describedAs)
        {
            try
            {
                var provider = MetadataReaderProvider.FromPortablePdbStream(
                    new MemoryStream(pdb, writable: false));
                return new PortablePdbReader(null, provider, describedAs);
            }
            catch
            {
                return null;
            }
        }

        public static PortablePdbReader? Open(string modulePath)
        {
            if (!File.Exists(modulePath))
                return null;

            PEReader? peReader = null;
            try
            {
                // Read the image into memory and let go of the file: holding the assembly open for
                // the life of the session would lock the user's build output, and rebuilding while
                // debugging is the entire point of the hot reload paths.
                using (var image = File.OpenRead(modulePath))
                    peReader = new PEReader(image, PEStreamOptions.PrefetchEntireImage);

                if (!peReader.TryOpenAssociatedPortablePdb(modulePath, OpenIfExists, out var provider, out var pdbPath) ||
                    provider is null)
                {
                    peReader.Dispose();
                    return null;
                }

                // An external PDB has its own stream, so nothing needs the image any more and
                // holding a prefetched copy of every module for the session would be pure waste.
                // Only the embedded case has to keep it, because the PDB is a range inside it.
                if (pdbPath is not null)
                {
                    peReader.Dispose();
                    return new PortablePdbReader(null, provider, pdbPath);
                }

                return new PortablePdbReader(peReader, provider, modulePath + " (embedded)");
            }
            catch
            {
                peReader?.Dispose();
                throw;
            }
        }

        private static Stream? OpenIfExists(string path)
        {
            try
            {
                return File.Exists(path) ? File.OpenRead(path) : null;
            }
            catch
            {
                // An unreadable candidate is simply not a candidate.
                return null;
            }
        }

        public void Dispose()
        {
            _provider.Dispose();
            _peReader?.Dispose();
        }
    }

    private sealed class SymbolReader : IDisposable
    {
        private SymbolReader(
            SymUnmanagedReader? unmanaged,
            PortablePdbReader? portable,
            string origin,
            string path,
            DecompiledSymbolSet? decompiled = null)
        {
            Unmanaged = unmanaged;
            Portable = portable;
            Origin = origin;
            SymbolPath = path;
            Decompiled = decompiled;
        }

        public SymUnmanagedReader? Unmanaged { get; }
        public PortablePdbReader? Portable { get; }

        /// <summary>
        /// Symbols recovered from the module itself rather than read from a PDB, for a module that
        /// shipped without one. Set on this reader and not beside it so that every lookup asks the
        /// same object it would have asked with a PDB present.
        /// </summary>
        /// <remarks>
        /// The set is the session's live one, not a copy: it gains a type each time the host
        /// decompiles one, and a reader holding a snapshot would answer for the frame that caused
        /// it to be created and nothing after.
        /// </remarks>
        public DecompiledSymbolSet? Decompiled { get; }

        /// <summary>Symbols for a module that has none, built from its own IL.</summary>
        public static SymbolReader FromDecompiled(DecompiledSymbolSet decompiled) =>
            new(null, null, SymbolOrigins.Decompiled, string.Empty, decompiled);

        /// <summary>
        /// Which edits this reader has already been told about, by their sequence number.
        /// </summary>
        /// <remarks>
        /// A delta may reach the same reader twice — once for the edit itself and again when a
        /// module reloads and the session replays its history into the new instance. Applying it
        /// twice would shift every line in the file by twice what the edit actually moved, so the
        /// reader remembers rather than the caller guessing.
        /// </remarks>
        public HashSet<long> AppliedEdits { get; } = [];

        /// <summary>Which kind of symbols these are — one of <see cref="SymbolOrigins"/>.</summary>
        public string Origin { get; }

        /// <summary>The file the symbols were actually read from. Empty when they never were a
        /// file: embedded in the module, or handed over by the runtime.</summary>
        public string SymbolPath { get; }

        /// <summary>
        /// Opens the symbols for a module, saying what happened when it cannot.
        /// </summary>
        /// <param name="status">One of <see cref="SymbolStatuses"/>, describing the outcome.</param>
        /// <param name="detail">Why no reader came back, phrased for the user. Empty on success.
        /// Reported rather than swallowed because "no symbols" has several causes with different
        /// fixes, and the difference between them is only visible here.</param>
        public static SymbolReader? Open(
            string modulePath, MetaDataImport metadata, out string status, out string detail)
        {
            status = SymbolStatuses.Loaded;
            detail = string.Empty;
            string? windowsProblem = null;
            try
            {
                var unmanaged = CreateUnmanagedSymbolReader(modulePath, metadata);
                return new SymbolReader(
                    unmanaged, null, SymbolOrigins.WindowsPdb, SiblingPdbIfPresent(modulePath));
            }
            catch (Exception ex)
            {
                // Expected for every portable-PDB module, which is most of them — so it is only
                // reported if the portable attempt below also comes up empty.
                windowsProblem = ex.Message;
            }

            try
            {
                var portable = PortablePdbReader.Open(modulePath);
                if (portable is not null)
                {
                    bool embedded = portable.Path.EndsWith("(embedded)", StringComparison.Ordinal);
                    return new SymbolReader(
                        null, portable,
                        embedded ? SymbolOrigins.EmbeddedPdb : SymbolOrigins.PortablePdb,
                        embedded ? string.Empty : portable.Path);
                }
            }
            catch (Exception ex)
            {
                status = SymbolStatuses.Rejected;
                detail = $"the module's portable PDB could not be read: {ex.Message}";
                return null;
            }

            if (SiblingPdbIfPresent(modulePath) is { Length: > 0 } sibling)
            {
                status = SymbolStatuses.Rejected;
                detail =
                    $"{System.IO.Path.GetFileName(sibling)} sits beside the module but does not " +
                    "belong to it — the usual cause is a PDB left from an earlier build, which a " +
                    "rebuild fixes" +
                    (windowsProblem is { Length: > 0 } ? $" ({windowsProblem})" : string.Empty);
            }
            else
            {
                status = SymbolStatuses.NotFound;
                detail = "the module records no PDB, carries none embedded, and none was found " +
                         "beside it";
            }

            return null;
        }

        /// <summary>The <c>.pdb</c> next to a module, when there is one. Only ever used to explain
        /// a failure: the readers locate symbols properly, and this guess is what the user sees in
        /// their output directory.</summary>
        private static string SiblingPdbIfPresent(string modulePath)
        {
            try
            {
                var sibling = System.IO.Path.ChangeExtension(modulePath, ".pdb");
                return File.Exists(sibling) ? sibling : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// A reader over symbols the runtime handed over as bytes rather than as a file.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Dynamically emitted modules have no PDB on disk to open — the symbols exist only in the
        /// debuggee and reach the debugger through the runtime's symbol-update callback. Without
        /// this, nothing generated at run time is debuggable: expression trees, generated
        /// serializer assemblies, in-memory view and Razor compilation, and anything else built
        /// through <c>Reflection.Emit</c>.
        /// </para>
        /// <para>
        /// Both PDB formats arrive here and they need different readers, told apart by the header
        /// the format itself carries. Portable symbols are read straight from the bytes. A Windows
        /// PDB has to go through diasymreader, which only opens files, so it is spilled to a
        /// temporary one — the caller owns deleting it, which is why the path comes back with the
        /// reader.
        /// </para>
        /// </remarks>
        public static (SymbolReader Reader, string? TempFile)? FromBytes(
            byte[] pdb, string moduleName, MetaDataImport metadata)
        {
            if (pdb.Length < 4)
                return null;

            // "BSJB": the metadata signature every portable PDB starts with.
            if (pdb[0] == 0x42 && pdb[1] == 0x53 && pdb[2] == 0x4A && pdb[3] == 0x42)
            {
                var portable = PortablePdbReader.FromBytes(pdb, $"{moduleName} (supplied at run time)");
                return portable is null
                    ? null
                    : (new SymbolReader(null, portable, SymbolOrigins.Runtime, string.Empty), null);
            }

            string? temp = null;
            try
            {
                temp = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    $"roslyn-sense-dynamic-{Guid.NewGuid():N}.pdb");
                File.WriteAllBytes(temp, pdb);

                var reader = CreateUnmanagedSymbolReaderFromPdb(temp, metadata);
                if (reader is null)
                {
                    TryDelete(temp);
                    return null;
                }

                return (new SymbolReader(reader, null, SymbolOrigins.Runtime, temp), temp);
            }
            catch
            {
                if (temp is not null)
                    TryDelete(temp);
                return null;
            }
        }

        internal static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { }
        }

        /// <summary>
        /// Closes both kinds of reader, including the COM one.
        /// </summary>
        /// <remarks>
        /// <c>ISymUnmanagedDispose::Destroy</c> is what actually makes diasymreader let go of the
        /// PDB file; dropping the reference alone leaves it open until some later collection, and
        /// possibly not even then. That matters beyond tidiness: a PDB spilled to a temporary file
        /// for a runtime-supplied Windows symbol store cannot be deleted while the reader holds it,
        /// so without this the session leaks one file per dynamic module — the exact leak the
        /// spill-and-delete was written to avoid.
        /// </remarks>
        public void Dispose()
        {
            Portable?.Dispose();

            if (Unmanaged is not { } unmanaged)
                return;

            try
            {
                if (unmanaged.Raw is ISymUnmanagedDispose disposable)
                    disposable.Destroy();
            }
            catch
            {
                // A reader that refuses to close is not a reason to abandon the rest of teardown.
            }

            try
            {
                if (Marshal.IsComObject(unmanaged.Raw))
                    Marshal.ReleaseComObject(unmanaged.Raw);
            }
            catch
            {
            }
        }
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
        if (reader.Decompiled is { } decompiled)
        {
            // Every method decompiled into the file is asked, and the shared chooser picks between
            // them — the same way the unmanaged path asks every method in a document.
            var candidates = new List<ResolvedSequencePoint>();
            foreach (var token in decompiled.MethodsIn(document.Url))
            {
                if (decompiled.BestPoint(token, requestedLine, requestedColumn) is not { } found)
                    continue;
                var (point, file) = found;
                candidates.Add(new ResolvedSequencePoint(
                    new mdMethodDef(token),
                    new SequencePointMatch(
                        point.Offset, point.Line, point.Column,
                        point.EndLine, point.EndColumn, file)));
            }

            return BestCandidate(candidates, requestedLine);
        }

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
        if (reader.Decompiled is { } decompiled)
        {
            // The method has to be in the document that was asked about. A module accumulates a
            // decompiled file per type it is stopped in, and this method's own file is one of
            // several — matching a line number without checking would find the requested line in
            // whatever file this method happens to live in, and report the answer against the file
            // that was asked for. The caller is Set Next Statement, so a wrong answer moves the
            // instruction pointer.
            if (!SamePath(decompiled.FileOf((int)methodToken), document.Url))
                return null;
            if (decompiled.BestPoint((int)methodToken, line, column) is not { } found)
                return null;
            var (point, file) = found;
            return new SequencePointMatch(
                point.Offset, point.Line, point.Column, point.EndLine, point.EndColumn, file);
        }

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
        if (reader.Decompiled is { } decompiled)
        {
            if (decompiled.PointAt((int)methodToken, ip) is not { } found)
                return null;
            var (point, next, file) = found;
            return new SequencePointMatch(
                point.Offset, point.Line, point.Column, point.EndLine, point.EndColumn, file, next);
        }

        if (reader.Unmanaged is not null)
        {
            try
            {
                var method = reader.Unmanaged.GetMethod(methodToken);
                var points = method.GetSequencePoints(method.SequencePointCount);
                for (var i = 0; i < points.offsets.Length; i++)
                {
                    if (IsHiddenSequencePoint(points.lines[i]))
                        continue;

                    var start = points.offsets[i];

                    // Runs to the next point that is both visible and further along. Compiler
                    // generated IL — a `using` close, an iterator's plumbing — belongs to the
                    // statement it was generated for, so both the reported location and the step
                    // range span it; ending at it instead leaves a step stopped on a line that
                    // does not exist in the source. Points sharing an offset would otherwise give
                    // an empty range, which degrades a step to a single IL instruction. The
                    // portable reader gets both by dropping hidden points before it pairs offsets.
                    var next = i + 1;
                    while (next < points.offsets.Length &&
                           (IsHiddenSequencePoint(points.lines[next]) || points.offsets[next] <= start))
                    {
                        next++;
                    }

                    var end = next < points.offsets.Length ? points.offsets[next] : int.MaxValue;
                    if (ip < start || ip >= end)
                        continue;
                    var document = new SymUnmanagedDocument(points.documents[i]);
                    var file = Safe(() => document.URL) ?? string.Empty;
                    var column = i < points.columns.Length ? points.columns[i] : 0;
                    var endLine = i < points.endLines.Length && points.endLines[i] != 0 ? points.endLines[i] : points.lines[i];
                    var endColumn = i < points.endColumns.Length ? points.endColumns[i] : 0;
                    return new SequencePointMatch(
                        start, points.lines[i], column, endLine, endColumn, file, end);
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
                    PortableSequencePointFile(reader.Portable.Reader, method, point),
                    end);
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

        if (reader.Decompiled is { } decompiled)
        {
            var requested = SourceRangeOf(request.FilePath, (int)request.Line, (int)request.Column);
            foreach (var (_, point) in decompiled.PointsIn(document.Value.Url))
            {
                if (point.Line > (int)request.Line ||
                    (point.EndLine == 0 ? point.Line : point.EndLine) < (int)request.Line)
                {
                    continue;
                }

                yield return new BreakpointLocation
                {
                    Id = $"{Path.GetFullPath(request.FilePath).ToLowerInvariant()}:{point.Line}:{point.Column}",
                    Requested = requested,
                    Actual = SourceRangeOf(
                        document.Value.Url, point.Line, point.Column, point.EndLine, point.EndColumn),
                    Verified = true,
                    Message = string.Empty,
                    Label = point.Column > 0 ? $"column {point.Column}" : $"line {point.Line}",
                    Kind = BreakpointKind.Source,
                };
            }
            yield break;
        }

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
                file = document.Url;
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
    {
        // SourceKey full-paths its input and an empty path throws — and a breakpoint stop whose
        // location did not resolve (entry breakpoints, a stale event) still has to be reported,
        // not die inside the managed callback.
        if (filePath.Length == 0)
        {
            spec = null!;
            return false;
        }
        return _boundSpecs.TryGetValue(SourceKey(filePath, line), out spec!);
    }

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

    /// <summary>
    /// Every native breakpoint standing for one breakpoint the user set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One source line is not one place in the process. A hosted application loads the same
    /// assembly into every app domain it serves, and each of those is a separate module with a
    /// separate breakpoint to arm; a source file compiled into two projects is two methods. Placing
    /// one and stopping — which is what a single breakpoint per line amounts to — makes a
    /// breakpoint that works in the first request's app domain and silently does nothing in the
    /// second.
    /// </para>
    /// <para>
    /// The user still sees one breakpoint: the placements are an implementation detail, and every
    /// report, condition and hit count is keyed by the source line as before.
    /// </para>
    /// </remarks>
    private sealed class BoundBreakpoint
    {
        private readonly object _gate = new();

        /// <summary>Keyed by module instance — the same image in two app domains is two of
        /// them.</summary>
        private readonly Dictionary<string, Placement> _placements =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>A breakpoint of null is a reservation: a bind is in flight for this instance
        /// and has not armed anything yet.</summary>
        private readonly record struct Placement(string ModulePath, CorDebugFunctionBreakpoint? Breakpoint);

        /// <summary>Set once this has been torn down, so a bind that was already in flight arms
        /// nothing that nobody is left holding.</summary>
        private bool _abandoned;

        /// <summary>
        /// Claims this instance for a bind that is about to run.
        /// </summary>
        /// <remarks>
        /// Test and claim in one step. Two threads reach a bind for the same module — the session
        /// thread's sweep and the runtime's module-load callback both do — and checking first,
        /// arming second would let both arm, with only the second one remembered and the first left
        /// armed in the debuggee with nothing tracking it.
        /// </remarks>
        public bool TryReserve(string instanceKey, string modulePath)
        {
            lock (_gate)
            {
                if (_abandoned || _placements.ContainsKey(instanceKey))
                    return false;
                _placements[instanceKey] = new Placement(modulePath, null);
                return true;
            }
        }

        /// <summary>
        /// Records what the bind armed.
        /// </summary>
        /// <returns>How many instances this breakpoint is now armed in, so the caller can tell the
        /// first placement — which the client should hear about — from the rest, which are the same
        /// breakpoint in another app domain. Zero when the session tore down mid-bind, in which case
        /// the breakpoint has been disarmed again rather than left behind.</returns>
        public int Complete(string instanceKey, CorDebugFunctionBreakpoint breakpoint)
        {
            lock (_gate)
            {
                if (_abandoned || !_placements.TryGetValue(instanceKey, out var reservation))
                {
                    Disarm(breakpoint);
                    return 0;
                }

                _placements[instanceKey] = reservation with { Breakpoint = breakpoint };
                return _placements.Values.Count(p => p.Breakpoint is not null);
            }
        }

        /// <summary>Gives up a reservation whose bind did not arm anything.</summary>
        public void Release(string instanceKey)
        {
            lock (_gate)
            {
                if (_placements.TryGetValue(instanceKey, out var placement) && placement.Breakpoint is null)
                    _placements.Remove(instanceKey);
            }
        }

        /// <summary>Whether anything is armed here, as opposed to merely reserved.</summary>
        public bool IsArmed
        {
            get
            {
                lock (_gate)
                    return _placements.Values.Any(p => p.Breakpoint is not null);
            }
        }

        /// <summary>Whether any placement lives in a module of this simple assembly name.</summary>
        public bool InAssembly(string assemblyName)
        {
            lock (_gate)
            {
                return _placements.Values.Any(p => string.Equals(
                    Path.GetFileNameWithoutExtension(p.ModulePath), assemblyName,
                    StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>
        /// Disarms and forgets the placement in one module instance.
        /// </summary>
        /// <remarks>
        /// By instance, not by image. The same assembly in two app domains is the case this whole
        /// type exists for, and both have the same path — removing by path on one domain's unload
        /// would disarm the breakpoint the other domain is still stopping at, and report the user's
        /// breakpoint as unbound while it is armed.
        /// </remarks>
        /// <param name="nowEmpty">Whether nothing is left, which is when the user's breakpoint has
        /// genuinely gone back to pending rather than merely losing one of its homes.</param>
        /// <returns>Whether this breakpoint was placed in that instance at all.</returns>
        public bool RemoveInstance(string instanceKey, out bool nowEmpty)
        {
            lock (_gate)
            {
                bool removed = _placements.Remove(instanceKey, out var placement);
                if (removed && placement.Breakpoint is { } breakpoint)
                    Disarm(breakpoint);

                nowEmpty = _placements.Count == 0;
                return removed;
            }
        }

        /// <summary>
        /// Disarms and forgets every placement that came from this image, whichever domain it was
        /// loaded into.
        /// </summary>
        /// <remarks>
        /// The fallback for an unload whose module can no longer be identified — by then the
        /// runtime may refuse to hand back anything about it. Coarser than
        /// <see cref="RemoveInstance"/> and wrong in the two-app-domain case, but the alternative is
        /// leaving a breakpoint armed in an image that is gone, which is what makes a later detach
        /// fail.
        /// </remarks>
        public bool RemoveModule(string modulePath, out bool nowEmpty)
        {
            lock (_gate)
            {
                var gone = _placements
                    .Where(p => string.Equals(p.Value.ModulePath, modulePath, StringComparison.OrdinalIgnoreCase))
                    .Select(p => p.Key)
                    .ToList();

                foreach (var instanceKey in gone)
                {
                    if (_placements.Remove(instanceKey, out var placement) &&
                        placement.Breakpoint is { } breakpoint)
                    {
                        Disarm(breakpoint);
                    }
                }

                nowEmpty = _placements.Count == 0;
                return gone.Count > 0;
            }
        }

        public void DeactivateAll()
        {
            lock (_gate)
            {
                _abandoned = true;
                foreach (var placement in _placements.Values)
                {
                    if (placement.Breakpoint is { } breakpoint)
                        Disarm(breakpoint);
                }
                _placements.Clear();
            }
        }

        /// <remarks>Left armed, a removed breakpoint keeps stopping the target, and a leaked native
        /// breakpoint is what later makes detach fail.</remarks>
        private static void Disarm(CorDebugFunctionBreakpoint breakpoint)
        {
            try { breakpoint.Activate(false); } catch { }
        }
    }

    /// <summary>
    /// Claims one module instance for a bind, creating the breakpoint's home if it has none yet.
    /// </summary>
    /// <returns>Null when this instance is already taken — bound, or being bound right now.</returns>
    private BoundBreakpoint? Reserve(string key, string instanceKey, string modulePath)
    {
        var bound = _bound.GetOrAdd(key, _ => new BoundBreakpoint());
        return bound.TryReserve(instanceKey, modulePath) ? bound : null;
    }

    /// <summary>
    /// Gives up a reservation, and the whole entry with it when nothing else claimed it.
    /// </summary>
    /// <remarks>
    /// An entry left behind with nothing armed in it is not merely untidy: a later bind sweep would
    /// find it and take it for a breakpoint that is already placed.
    /// </remarks>
    private void ReleaseReservation(string key, string instanceKey)
    {
        if (!_bound.TryGetValue(key, out var bound))
            return;

        bound.Release(instanceKey);
        if (!bound.IsArmed)
            _bound.TryRemove(new KeyValuePair<string, BoundBreakpoint>(key, bound));
    }

    private void TryBindBreakpoint(CorDebugModule module, BreakpointSpec spec)
    {
        // A generated App_Web_*.dll can only satisfy a markup breakpoint; probing its PDB for
        // anything else is a per-module, per-spec cost a WebForms site pays dozens of times over.
        if (spec.MethodToken == 0 &&
            !TargetsMarkup(spec) &&
            IsGeneratedWebFormsModule(Safe(() => module.Name) ?? string.Empty))
        {
            return;
        }

        if (spec.MethodToken != 0)
            TryBindIlBreakpoint(module, spec);
        else if (spec.FilePath.Length > 0)
            TryBindSourceBreakpoint(module, spec);
        else
            TryBindEntryBreakpoint(module, spec);
    }

    /// IL-form binding, for breakpoints in decompiled or fetched external source: the file the
    /// user pointed at appears in no PDB, but the host already mapped the line to a MethodDef
    /// token and IL offset, so the breakpoint goes straight onto the IL. The spec keeps the
    /// external file and line for reporting and removal.
    private void TryBindIlBreakpoint(CorDebugModule module, BreakpointSpec spec)
    {
        var key = SourceKey(spec.FilePath, (int)spec.Line);
        var moduleName = Safe(() => module.Name) ?? string.Empty;
        var instanceKey = Safe(() => InstanceKey(module)) ?? string.Empty;
        if (instanceKey.Length == 0 || Reserve(key, instanceKey, moduleName) is not { } bound)
            return;

        var armed = false;
        try
        {
            if (!ModuleMatchesSpec(moduleName, spec.ModulePath))
                return;

            var function = module.GetFunctionFromToken((mdMethodDef)spec.MethodToken);
            var breakpoint = function.ILCode.CreateBreakpoint(spec.IlOffset);
            breakpoint.Activate(true);
            int placements = bound.Complete(instanceKey, breakpoint);
            armed = placements > 0;
            if (!armed)
                return;

            spec.Kind = spec.Kind == BreakpointKind.Unspecified ? BreakpointKind.Source : spec.Kind;
            var location = SourceRangeOf(spec.FilePath, (int)spec.Line, (int)spec.Column);
            _boundModule[key] = moduleName;
            _boundSpecs[key] = spec;
            _boundKeyByRequest[key] = key;
            // Only the first placement is news. The rest are the same breakpoint in another app
            // domain, and reporting each one would draw the gutter as though the user had set
            // several.
            if (placements > 1)
                return;
            Emit(
                DebugEventKind.BreakpointBound,
                $"bound at IL offset 0x{spec.IlOffset:X} in {Path.GetFileName(moduleName)}",
                string.Empty,
                0,
                location.FilePath,
                (int)location.Line,
                (int)location.Column,
                requestedLocation: location,
                actualLocation: location,
                breakpointId: spec.Id);
        }
        catch (Exception ex)
        {
            // The module name already matched the spec, so this is the named module failing to
            // take the breakpoint rather than an unrelated one being skipped.
            ReportBindFailure(spec, moduleName, DescribeBindFailure(ex));
        }
        finally
        {
            if (!armed)
                ReleaseReservation(key, instanceKey);
        }
    }

    /// <summary>Whether a loaded module is the one an IL-form spec names. Shadow copies keep the
    /// file name but move the directory, so the fallback compares names only.</summary>
    private static bool ModuleMatchesSpec(string moduleName, string specModulePath)
    {
        if (moduleName.Length == 0 || specModulePath.Length == 0)
            return false;
        if (string.Equals(moduleName, specModulePath, StringComparison.OrdinalIgnoreCase))
            return true;
        return string.Equals(
            Path.GetFileName(moduleName), Path.GetFileName(specModulePath),
            StringComparison.OrdinalIgnoreCase);
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
        var moduleName = Safe(() => module.Name) ?? string.Empty;
        var instanceKey = Safe(() => InstanceKey(module)) ?? string.Empty;
        if (instanceKey.Length == 0 || Reserve(requestedKey, instanceKey, moduleName) is not { } reserved)
            return;

        string armedKey = string.Empty;
        try
        {
            if (moduleName.Length == 0)
                return;
            var reader = ReaderFor(module, moduleName);
            if (reader is null)
                return;

            // Not finding the document is the ordinary case — most loaded modules have nothing to
            // do with this file — so it stays silent. Everything past this point is a module that
            // does own the file, where a failure is worth explaining.
            var document = FindDocument(reader, spec.FilePath, out string mismatch);
            if (document is null)
            {
                if (mismatch.Length > 0)
                    ReportBindFailure(spec, moduleName, mismatch);
                return;
            }

            var resolved = BestSequencePointInDocument(reader, document.Value, requestedLine, requestedColumn);
            if (resolved is null)
            {
                ReportBindFailure(
                    spec, moduleName,
                    $"it has no executable code at or after line {requestedLine}");
                return;
            }
            var match = resolved.Value.Match;
            var actual = SourceRangeOf(
                spec.FilePath,
                match.Line,
                match.Column,
                match.EndLine,
                match.EndColumn);
            // The sequence point can sit below the requested line, in which case the breakpoint
            // lives under a different key than the one reserved above.
            var actualKey = SourceKey(spec.FilePath, match.Line);
            BoundBreakpoint bound;
            if (actualKey == requestedKey)
            {
                bound = reserved;
            }
            else if (Reserve(actualKey, instanceKey, moduleName) is { } moved)
            {
                bound = moved;
            }
            else
            {
                return;
            }

            var function = module.GetFunctionFromToken(resolved.Value.MethodToken);
            var breakpoint = function.ILCode.CreateBreakpoint(match.Offset);
            breakpoint.Activate(true);
            int placements = bound.Complete(instanceKey, breakpoint);
            if (placements == 0)
            {
                if (actualKey != requestedKey)
                    ReleaseReservation(actualKey, instanceKey);
                return;
            }

            armedKey = actualKey;
            spec.Line = (uint)match.Line;
            spec.Column = (uint)match.Column;
            spec.EndLine = (uint)match.EndLine;
            spec.EndColumn = (uint)match.EndColumn;
            spec.Kind = spec.Kind == BreakpointKind.Unspecified ? BreakpointKind.Source : spec.Kind;
            _boundModule[actualKey] = moduleName;
            _boundSpecs[actualKey] = spec;
            _boundKeyByRequest[requestedKey] = actualKey;
            // Every placement past the first is the same breakpoint in another app domain.
            if (placements > 1)
                return;
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
        catch (Exception ex)
        {
            // The module owned the document, so this is a real failure rather than the usual
            // "wrong module" case — say why instead of leaving the breakpoint silently grey.
            ReportBindFailure(spec, moduleName, DescribeBindFailure(ex));
        }
        finally
        {
            // The requested key holds a reservation that only the requested line ever redeems; when
            // the breakpoint landed elsewhere — or nowhere — it has to be given back.
            if (armedKey != requestedKey)
                ReleaseReservation(requestedKey, instanceKey);
        }
    }

    /// <summary>
    /// Reports why a breakpoint could not be placed in a module that does contain its source file.
    /// </summary>
    /// <remarks>
    /// Deduplicated per breakpoint and module: binding is retried on every module load and after
    /// every applied edit, so without this the same failure would be repeated for the life of the
    /// session.
    /// </remarks>
    private void ReportBindFailure(BreakpointSpec spec, string moduleName, string reason)
    {
        // The reason is part of the key so that a *different* failure at the same place — the usual
        // case after a rebuild, where the module path is unchanged — is still reported.
        if (!_reportedBindFailures.TryAdd($"{spec.Id}|{moduleName}|{reason}", 0))
            return;

        var where = moduleName.Length > 0 ? Path.GetFileName(moduleName) : "the module";
        Emit(
            DebugEventKind.Diagnostic,
            $"breakpoint at {Path.GetFileName(spec.FilePath)}:{spec.Line} did not bind in {where}: {reason}",
            string.Empty, 0);
    }

    /// <summary>Turns a binding failure into something worth reading.</summary>
    private static string DescribeBindFailure(Exception ex) => (HRESULT)ex.HResult switch
    {
        HRESULT.CORDBG_E_UNABLE_TO_SET_BREAKPOINT =>
            "the runtime rejected that position; it is not the start of a statement",
        HRESULT.CORDBG_E_CODE_NOT_AVAILABLE =>
            "the method has no code yet — it may not have been reached, or the module is not fully loaded",
        HRESULT.CORDBG_E_FUNCTION_NOT_IL =>
            "the method has no IL body to place a breakpoint in",
        HRESULT.CORDBG_E_MODULE_NOT_LOADED =>
            "the module is no longer loaded",
        HRESULT.CORDBG_E_PROCESS_TERMINATED =>
            "the process has exited",
        _ => ex.Message,
    };

    private void TryBindEntryBreakpoint(CorDebugModule module, BreakpointSpec spec)
    {
        var moduleName = Safe(() => module.Name) ?? string.Empty;
        var key = $"{moduleName}!{spec.TypeName}.{spec.MethodName}";
        var instanceKey = Safe(() => InstanceKey(module)) ?? moduleName;
        // The place is reserved before the search so the same module instance cannot bind this
        // entry point twice. Another instance of the same module — a second app domain — reserves
        // its own place under the same key and gets its own breakpoint.
        if (Reserve(key, instanceKey, moduleName) is not { } bound)
            return;
        var created = false;
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
                    int placements = bound.Complete(instanceKey, breakpoint);
                    if (placements == 0)
                        return;
                    _boundModule[key] = moduleName;
                    created = true;
                    // Every placement past the first is the same entry point in another app domain.
                    if (placements == 1)
                        Emit(DebugEventKind.Diagnostic, $"bound breakpoint {typeProps.szTypeDef}.{methodProps.szMethod}", methodProps.szMethod, 0);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[debug] bind failed: {ex.Message}");
        }
        finally
        {
            // A reservation left behind blocks every later attempt at this key and hands removal
            // a null breakpoint.
            if (!created)
                ReleaseReservation(key, instanceKey);
        }
    }

    // --- symbols --------------------------------------------------------------------------------

    /// <summary>
    /// The symbol reader for a module, opened once and cached.
    /// </summary>
    /// <remarks>
    /// Locked because this is not session-thread-only, whatever the field's neighbours suggest:
    /// the runtime's callback thread reaches it through <c>OnLoadModule</c> and through the
    /// location lookup every stop does, while the session thread reaches it from the bind sweep
    /// and from every stack, variable and module query. <see cref="Dictionary{K,V}"/> tears under
    /// that, and diasymreader's readers are not free-threaded either.
    /// </remarks>
    private SymbolReader? ReaderFor(CorDebugModule module, string moduleName)
    {
        // Checked before the cache and never stored in it: the globs can change mid-session,
        // and a cached null would pin the old policy until the module reloads.
        if (!SymbolGlobs.WantsSymbols(_display, moduleName))
        {
            _symbolStatus[moduleName] = new SymbolStatusEntry(
                SymbolStatuses.Excluded, string.Empty, string.Empty,
                "the symbol settings exclude this module, so no PDB was looked for");
            return null;
        }

        lock (_readers)
        {
            if (_readers.TryGetValue(moduleName, out var cached))
                return cached ?? DecompiledReaderFor(moduleName);
        }
        SymbolReader? reader = null;
        string status, detail;
        try
        {
            var metadata = Extensions.GetMetaDataInterface<MetaDataImport>(module);
            reader = SymbolReader.Open(moduleName, metadata, out status, out detail);
        }
        catch (Exception ex)
        {
            status = SymbolStatuses.Rejected;
            detail = $"the module's symbols could not be opened: {ex.Message}";
        }

        _symbolStatus[moduleName] = reader is not null
            ? new SymbolStatusEntry(
                SymbolStatuses.Loaded, reader.Origin, reader.SymbolPath, string.Empty)
            : new SymbolStatusEntry(status, string.Empty, string.Empty, detail);

        if (reader is null)
            ReportUnusablePdb(moduleName);

        lock (_readers)
        {
            // Another thread may have opened one while this was reading the PDB; theirs wins, so
            // a single reader per module is handed out and this one is closed rather than leaked.
            if (_readers.TryGetValue(moduleName, out var raced))
            {
                reader?.Dispose();
                return raced ?? DecompiledReaderFor(moduleName);
            }

            _readers[moduleName] = reader;
        }
        return reader ?? DecompiledReaderFor(moduleName);
    }

    /// <summary>Decompiled source for a module the module names, if any has been handed over.</summary>
    /// <remarks>
    /// Reached only where a PDB was looked for and not found, so a real PDB always wins: symbols
    /// the compiler wrote name the author's own file and the author's own lines, and decompiled
    /// ones never can.
    /// </remarks>
    private SymbolReader? DecompiledReaderFor(string moduleName)
    {
        if (moduleName.Length == 0 || !_decompiledSymbols.TryGetValue(moduleName, out var symbols))
            return null;
        if (symbols.IsEmpty)
            return null;

        // One reader per module, cached because the set behind it is live — a new reader per call
        // would answer identically and cost an allocation on every frame of every stop.
        var reader = _decompiledReaders.GetOrAdd(moduleName, _ => SymbolReader.FromDecompiled(symbols));

        // Unless it is over a set that has since been thrown away. A module that unloaded between
        // the lookup above and this line leaves a reader behind whose set nothing writes to any
        // more, and it would answer from the old build for the rest of the session.
        if (!ReferenceEquals(reader.Decompiled, symbols))
        {
            reader = SymbolReader.FromDecompiled(symbols);
            _decompiledReaders[moduleName] = reader;
        }

        _symbolStatus.AddOrUpdate(
            moduleName,
            new SymbolStatusEntry(
                SymbolStatuses.Loaded, SymbolOrigins.Decompiled, string.Empty, string.Empty),
            // The detail said why no PDB was found, which is still true and still the thing to fix
            // if the user wants their own source back.
            (_, previous) => previous with
            {
                Status = SymbolStatuses.Loaded,
                Origin = SymbolOrigins.Decompiled,
                Path = string.Empty,
            });

        return reader;
    }

    /// <summary>
    /// Takes in one decompiled type as symbols for a module.
    /// </summary>
    /// <remarks>
    /// Pushed rather than pulled. The decompiler lives in the host — the engine may be a separate
    /// process, and even in-process it has no business holding one — so the engine cannot ask for a
    /// type at the moment it needs one, and a synchronous call back into the host from the callback
    /// thread would be a deadlock waiting for a slow answer. The host already decompiles the frames
    /// of every stop before the user can act on them, so by the time a step or a breakpoint needs
    /// the symbols, they are here.
    /// </remarks>
    public void AddDecompiledSymbols(string modulePath, DecompiledSymbolMap map)
    {
        if (modulePath.Length == 0 || map.IsEmpty)
            return;

        _decompiledSymbols.GetOrAdd(modulePath, _ => new DecompiledSymbolSet()).Add(map);
    }

    /// <summary>
    /// Drops a module's decompiled symbols, for a module that unloaded or whose IL has moved on.
    /// </summary>
    /// <remarks>
    /// They describe one build of one file. A module reloaded from the same path — a plugin
    /// rebuilt, an app domain recycled — is a different build behind the same name, and offsets
    /// from the old one point into instructions that are no longer where they were. Nothing
    /// replaces them by itself: the set merges per method, so a method the new build no longer has
    /// would keep answering forever. Cheap to lose — the host decompiles again at the next stop.
    /// </remarks>
    private void ForgetDecompiledSymbols(string modulePath)
    {
        if (modulePath.Length == 0)
            return;

        // The reader goes first, so a caller that has just looked one up cannot get a live reader
        // over a set that is about to be emptied.
        _decompiledReaders.TryRemove(modulePath, out _);
        _decompiledSymbols.TryRemove(modulePath, out _);
    }

    /// <summary>
    /// Reports a PDB that exists next to a module but could not be used for it.
    /// </summary>
    /// <remarks>
    /// Worth distinguishing from "no symbols at all", because the two look identical from the
    /// outside and have opposite fixes. The likeliest cause is a PDB left over from an earlier
    /// build, which the identity check rejects — but it is not the only one, so the message says
    /// what was observed rather than asserting a cause that was never confirmed. A Windows PDB
    /// that diasymreader declined, or a file briefly locked by another process, arrives here too.
    /// </remarks>
    private void ReportUnusablePdb(string moduleName)
    {
        var sibling = Safe(() => Path.ChangeExtension(moduleName, ".pdb"));
        if (string.IsNullOrEmpty(sibling) || Safe(() => File.Exists(sibling)) != true)
            return;
        if (!_reportedPdbProblems.TryAdd(moduleName, 0))
            return;

        Emit(
            DebugEventKind.Diagnostic,
            $"{Path.GetFileName(moduleName)} has a PDB beside it ({Path.GetFileName(sibling)}) that " +
            "could not be used, so breakpoints in this module cannot bind. The usual cause is a PDB " +
            "left from an earlier build, which a rebuild fixes.",
            string.Empty, 0);
    }

    /// <summary>Modules already reported as having an unusable PDB.</summary>
    private readonly ConcurrentDictionary<string, byte> _reportedPdbProblems = new();

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

    /// <summary>
    /// A Windows-PDB reader over a PDB file directly, rather than over the module beside it.
    /// </summary>
    /// <remarks>
    /// Used for symbols that never had a module to sit beside — the runtime handed them over for
    /// a dynamically emitted assembly, and the file they were spilled to is the only thing there
    /// is to point the binder at.
    /// </remarks>
    private static SymUnmanagedReader? CreateUnmanagedSymbolReaderFromPdb(
        string pdbPath, MetaDataImport metadata)
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
        var directory = Path.GetDirectoryName(pdbPath) ?? Environment.CurrentDirectory;

        // The binder matches a PDB to a module by name, so the module it is told about is the
        // temporary file's own stem — which is what the PDB was written as.
        var pretendModule = Path.ChangeExtension(pdbPath, ".dll");
        return binder.TryGetReaderForFile(metadata.Raw, pretendModule, directory, out var reader) == HRESULT.S_OK
            ? reader
            : null;
    }

    /// <summary>
    /// The PDB document that describes a local source file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Path equality answers this whenever the binary was built on this machine, and never
    /// otherwise. A build on CI, in a container, or with mapped source roots writes document paths
    /// that do not exist here, so comparing paths says "this module has nothing to do with that
    /// file" about the module that owns it — and a breakpoint that never binds, with nothing said
    /// about why, is the whole of the symptom.
    /// </para>
    /// <para>
    /// So the fallback matches by file name and confirms with the hash the PDB recorded, which is
    /// the only evidence that two differently-spelled paths are the same file. What it learns from
    /// the first confirmed file — that one directory prefix stands for another — is remembered and
    /// tried first for the rest, so the cost is paid once per build root rather than per file.
    /// </para>
    /// </remarks>
    private SymbolDocument? FindDocument(SymbolReader reader, string filePath) =>
        FindDocument(reader, filePath, out _);

    /// <param name="mismatch">Empty unless the module has a document with this file's name that the
    /// checksum ruled out. That is the one failure worth telling the user about — it means the
    /// binary was built from a different copy of the file they are looking at — and it is
    /// indistinguishable from "wrong module" without saying so.</param>
    /// <inheritdoc cref="FindDocument(SymbolReader, string)"/>
    private SymbolDocument? FindDocument(SymbolReader reader, string filePath, out string mismatch)
    {
        mismatch = string.Empty;

        string full;
        try { full = Path.GetFullPath(filePath); }
        catch { return null; }

        var name = Path.GetFileName(full);
        var candidates = new List<(SymbolDocument Document, int Shared)>();

        foreach (var candidate in DocumentsOf(reader))
        {
            if (candidate.Url.Length == 0)
                continue;

            if (SamePath(candidate.Url, full))
                return candidate;

            // A rewrite this session already confirmed, applied before anything is hashed.
            if (RewrittenMatches(candidate.Url, full))
                return candidate;

            if (string.Equals(Path.GetFileName(candidate.Url), name, StringComparison.OrdinalIgnoreCase))
                candidates.Add((candidate, SharedSuffixLength(candidate.Url, full)));
        }

        if (candidates.Count == 0)
            return null;

        // Longest shared tail first: "src/App/Program.cs" is a better guess than a bare "Program.cs"
        // from some unrelated corner of the build. But it is only an ordering — a project that
        // compiles a linked file from a sibling directory puts the right document lower down the
        // list, so every candidate gets its hash read rather than just the best-looking one.
        var refuted = string.Empty;
        foreach (var (document, _) in candidates.OrderByDescending(c => c.Shared))
        {
            if (SourceChecksum.Matches(full, document.ChecksumAlgorithm, document.Checksum))
            {
                LearnPathRewrite(document.Url, full);
                return document;
            }

            if (refuted.Length == 0 && document.Checksum is { Length: > 0 })
                refuted = document.Url;
        }

        // Only a hash that disagreed is worth reporting. A PDB that recorded none says nothing about
        // this file either way, and saying so once per module would bury the real failures.
        if (refuted.Length > 0)
            mismatch = $"it was built from a different copy of {name} ({refuted})";

        return null;
    }

    /// <summary>Every reader's documents behind one shape, so the matching is written once.</summary>
    private static IEnumerable<SymbolDocument> DocumentsOf(SymbolReader reader)
    {
        if (reader.Decompiled is { } decompiled)
        {
            // No checksum: this file was written here, from this module, moments ago. The path
            // matches exactly, so none of the hash-based reconciliation below is reached.
            foreach (var file in decompiled.Files)
                yield return new SymbolDocument(file, null, default);
            yield break;
        }

        if (reader.Unmanaged is not null)
        {
            SymbolDocument[] documents;
            try
            {
                documents = [.. reader.Unmanaged.Documents.Select(d => new SymbolDocument(
                    Safe(() => d.URL) ?? string.Empty,
                    d,
                    default,
                    // Left empty rather than read, and this is not an oversight to tidy up.
                    //
                    // ISymUnmanagedDocument's Guid out-parameters go through a marshaller whose
                    // only registration is for every direction at once, and whose shape is a
                    // pointer: an "out Guid" is handed to the callee as GuidNative**, while
                    // diasymreader writes a plain GUID* into it. The sixteen bytes of the guid
                    // land on the eight-byte slot holding the pointer, and the marshaller then
                    // dereferences what it finds there as an address. A guid of all zeros — the
                    // documented answer for "this document has no checksum" — dereferences null.
                    //
                    // The result is an access violation, which the Safe() around it could not
                    // catch even if it were still here: it is a corrupted-state exception, so it
                    // takes the whole worker down with the session on it. That is a debugger
                    // that dies while attaching to a .NET Framework process, which is the one
                    // target whose symbols come from this reader at all.
                    //
                    // Nothing is lost. The value's only consumer tells the three hash algorithms
                    // apart by the length of the checksum, which it already does when a PDB
                    // records a hash without naming one.
                    ChecksumAlgorithm: Guid.Empty,
                    Checksum: Safe(() => d.CheckSum)))];
            }
            catch
            {
                documents = [];
            }

            foreach (var document in documents)
                yield return document;
        }

        if (reader.Portable is null)
            yield break;

        SymbolDocument[] portable;
        try
        {
            var metadata = reader.Portable.Reader;
            portable = [.. metadata.Documents.Select(handle =>
            {
                var document = metadata.GetDocument(handle);
                return new SymbolDocument(
                    metadata.GetString(document.Name),
                    null,
                    handle,
                    document.HashAlgorithm.IsNil ? Guid.Empty : metadata.GetGuid(document.HashAlgorithm),
                    document.Hash.IsNil ? null : metadata.GetBlobBytes(document.Hash));
            })];
        }
        catch
        {
            portable = [];
        }

        foreach (var document in portable)
            yield return document;
    }

    private static bool SamePath(string url, string fullPath)
    {
        try
        {
            return string.Equals(Path.GetFullPath(url), fullPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // A document path that is not a path this platform can express — a Linux build read on
            // Windows — is exactly the case the suffix match below exists for.
            return false;
        }
    }

    private bool RewrittenMatches(string url, string fullPath)
    {
        var local = SourcePaths.Normalize(fullPath);
        foreach (var (localPrefix, pdbPrefix) in _pathRewrites)
        {
            if (!local.StartsWith(SourcePaths.Normalize(localPrefix), StringComparison.OrdinalIgnoreCase))
                continue;
            if (SameTail(url, pdbPrefix, local[localPrefix.Length..]))
                return true;
        }

        return false;
    }

    private static bool SameTail(string url, string pdbPrefix, string tail)
    {
        if (url.Length != pdbPrefix.Length + tail.Length)
            return false;

        var normalized = SourcePaths.Normalize(url);
        return normalized.StartsWith(SourcePaths.Normalize(pdbPrefix), StringComparison.OrdinalIgnoreCase) &&
            normalized[pdbPrefix.Length..].Equals(
                SourcePaths.Normalize(tail), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Records that one directory prefix stands for another, having just seen a file confirm it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The prefixes are whatever is left once the shared tail is removed, which is why this is only
    /// called after a checksum agreed: a guess here would silently redirect every other file in the
    /// project to the wrong place.
    /// </para>
    /// <para>
    /// Both are stored with their trailing separator, so a prefix is only ever matched at a
    /// directory boundary. Without it <c>D:\work</c> would prefix <c>D:\workspace\...</c> too, and
    /// an unrelated tree would be rewritten into this build's.
    /// </para>
    /// </remarks>
    private void LearnPathRewrite(string url, string fullPath)
    {
        int shared = SharedSuffixLength(url, fullPath);
        if (shared <= 1 || shared >= url.Length || shared >= fullPath.Length)
            return;

        // The shared tail starts at the separator, except where one path was consumed whole — and
        // there there is no prefix left to learn anything about.
        if (fullPath[^shared] is not ('/' or '\\') || url[^shared] is not ('/' or '\\'))
            return;

        _pathRewrites[fullPath[..^(shared - 1)]] = url[..^(shared - 1)];
    }

    private static int SharedSuffixLength(string url, string fullPath) =>
        SourcePaths.SharedSuffixLength(url, fullPath);

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
        var span = FrameSpan(frame);
        return (span.File, span.Line, span.Column);
    }

    /// <summary>
    /// The whole source span of the statement a frame's IP is inside, not only its start.
    /// </summary>
    /// <remarks>
    /// The end is what makes the location a statement rather than a point, which is what an
    /// active-statement report has to be: the compiler decides whether an edit is safe by asking
    /// whether it overlaps a statement that is currently executing, and a zero-width point at the
    /// statement's start overlaps almost nothing.
    /// </remarks>
    private (string File, int Line, int Column, int EndLine, int EndColumn) FrameSpan(CorDebugFrame frame)
    {
        try
        {
            if (frame is not CorDebugILFrame ilFrame)
                return (string.Empty, 0, 0, 0, 0);
            var function = frame.Function;
            var moduleName = function.Module.Name;
            var reader = ReaderFor(function.Module, moduleName);
            if (reader is null)
                return (string.Empty, 0, 0, 0, 0);
            var match = SequencePointAtOffset(reader, function.Token, ilFrame.IP.pnOffset);
            if (match is not null)
            {
                return (
                    match.Value.FilePath, match.Value.Line, match.Value.Column,
                    match.Value.EndLine, match.Value.EndColumn);
            }
        }
        catch
        {
        }
        return (string.Empty, 0, 0, 0, 0);
    }

    /// <summary>
    /// What identifies a frame's code when its module has no symbols: the module file, the
    /// method's token, and the IP's IL offset. The host resolves external source from these.
    /// </summary>
    private static (string ModulePath, int MethodToken, int IlOffset) FrameIdentity(CorDebugFrame frame)
    {
        try
        {
            if (frame is not CorDebugILFrame ilFrame)
                return ("", 0, -1);

            var function = frame.Function;
            var modulePath = Safe(() => function.Module.Name) ?? "";
            return modulePath.Length == 0
                ? ("", 0, -1)
                : (modulePath, (int)function.Token, (int)ilFrame.IP.pnOffset);
        }
        catch
        {
            return ("", 0, -1);
        }
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
                // The whole span of the statement, not one IL byte. StepRange runs until the IP
                // leaves the range, so a one-byte range is a single IL instruction — which lands
                // back on the line it started on, and is what made F10 look like it did nothing.
                range = new COR_DEBUG_STEP_RANGE
                {
                    startOffset = match.Value.Offset,
                    endOffset = EndOfStatement(function, match.Value),
                };
                return true;
            }
        }
        catch
        {
        }
        return false;
    }

    /// <summary>
    /// Where the statement at <paramref name="match"/> ends, in IL.
    /// </summary>
    /// <remarks>
    /// The next sequence point, except on the last statement of a method, where there is none and
    /// the statement runs to the end of the body. The method's IL size bounds it there, so the
    /// range stays a real span rather than reaching past the code it belongs to.
    /// </remarks>
    private static int EndOfStatement(CorDebugFunction function, SequencePointMatch match)
    {
        int methodEnd = Safe(() => (int)function.ILCode.Size) is > 0 and var size ? size : int.MaxValue;
        return match.NextOffset is > 0 and var next && next != int.MaxValue
            ? Math.Min(next, methodEnd)
            : methodEnd;
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
            // In an instance method the frame's argument 0 is `this`, and the declared parameters
            // follow it — parameter sequence numbers alone are one-based over the declared list,
            // so aligning them without the shift labels `this` with the first parameter's name and
            // every argument after it with its left neighbour's.
            try
            {
                var isStatic = metadata.GetMethodProps(function.Token).pdwAttr.HasFlag(CorMethodAttr.mdStatic);
                if (!isStatic)
                    args[0] = "this";
                var shift = isStatic ? -1 : 0;

                foreach (var parameterToken in Extensions.EnumParams(metadata, function.Token))
                {
                    var parameter = metadata.GetParamProps(parameterToken);
                    if (parameter.pulSequence > 0 && !string.IsNullOrEmpty(parameter.szName))
                        args.TryAdd(parameter.pulSequence + shift, parameter.szName);
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

    /// <summary>An array as VS heads it: element type and lengths — <c>int[3]</c>,
    /// <c>int[2,3]</c>, and <c>int[2][]</c> with the outer length inside the first bracket.</summary>
    private static string ArrayDisplayOf(CorDebugArrayValue array)
    {
        var rank = Safe(() => (int?)array.Rank) ?? 1;
        var lengths = rank > 1 && Safe(() => array.GetDimensions(rank)) is { } dimensions
            ? string.Join(",", dimensions)
            : (Safe(() => (int?)array.Count) ?? 0).ToString();

        var element = ElementTypeNameOf(array);
        var bracket = element.IndexOf('[');
        return bracket < 0
            ? $"{element}[{lengths}]"
            : $"{element[..bracket]}[{lengths}]{element[bracket..]}";
    }

    /// <summary>A delegate as VS shows it: <c>{Method = the method it points at}</c>, which is
    /// what anyone stopping on a callback actually wants to know.</summary>
    private string? DelegateDisplayOf(CorDebugValue value)
    {
        if (!_display.CallToString || _displayDepth >= MaxDisplayDepth || !IsDelegateValue(value))
            return null;

        _displayDepth++;
        try
        {
            var method = MemberValue(value, "Method", callOnly: false, out _);
            return method is null ? null : "{Method = " + DescribeValue(method) + "}";
        }
        finally
        {
            _displayDepth--;
        }
    }

    private static bool IsDelegateValue(CorDebugValue value)
    {
        foreach (var (_, metadata, typeDef) in TypeChain(value))
        {
            if (Safe(() => metadata.GetTypeDefProps(typeDef).szTypeDef) == "System.MulticastDelegate")
                return true;
        }
        return false;
    }

    /// <summary>
    /// The one-line rendering of a value: its literal for a primitive or string, its
    /// <c>DebuggerDisplay</c> when its type asks for one, and its type name otherwise.
    /// </summary>
    /// <param name="applyDisplay">Whether <c>DebuggerDisplay</c> may run. Off wherever the string
    /// is compared rather than read — a breakpoint condition tests <c>state == "Open"</c> against
    /// the value, not against whatever the type would like that value to look like.</param>
    private string DescribeValue(CorDebugValue value, bool applyDisplay = true)
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
            {
                var length = Safe(() => (int?)str.Length) ?? 0;
                var shown = Safe(() => str.GetString(Math.Min(length, MaxStringDisplayLength))) ?? string.Empty;
                return QuoteString(shown, truncated: length > MaxStringDisplayLength);
            }
            var scalar = TryReadScalar(dereferenced);
            if (scalar is not null)
                return scalar;
            // A boxed primitive whose unboxed object will not hand over its bits directly still
            // holds them in the primitive's own m_value field.
            if (PrimitiveElementTypeOf(TypeNameOf(dereferenced)) is not null &&
                Safe(() => FieldValue(dereferenced, "m_value")) is { } unboxed &&
                TryReadScalar(Dereference(unboxed)) is { } unboxedScalar)
                return unboxedScalar;
            if (NullableDisplayOf(dereferenced) is { } nullableDisplay)
                return nullableDisplay;
            if (dereferenced is CorDebugArrayValue array)
                return ArrayDisplayOf(array);
            if (EnumDisplayOf(dereferenced) is { } enumDisplay)
                return enumDisplay;
            if (WellKnownStructDisplayOf(dereferenced) is { } structDisplay)
                return structDisplay;
            if (applyDisplay && DisplayStringFor(value) is { } display)
                return display;
            if (applyDisplay && DelegateDisplayOf(value) is { } delegateDisplay)
                return delegateDisplay;
            if (applyDisplay && ToStringDisplayOf(value) is { } text)
                return text;
            // No display string: the type's own name says more than the element type ("Class")
            // the runtime reports for every object alike.
            if (TypeNameOf(value) is { Length: > 0 } typeName)
                return typeName;
            return Safe(() => dereferenced.Type.ToString()) ?? "?";
        }
        catch
        {
            return "?";
        }
    }

    /// <summary>
    /// A <c>Nullable&lt;T&gt;</c> is its value or "null" — never its type name, and never its
    /// <c>hasValue</c>/<c>value</c> fields, which are an implementation detail no debugger shows.
    /// </summary>
    private string? NullableDisplayOf(CorDebugValue value)
    {
        var typeName = TypeNameOf(value);
        if (typeName is not "System.Nullable`1" && !typeName.StartsWith("System.Nullable<", StringComparison.Ordinal))
            return null;

        var hasValue = Safe(() => FieldValue(value, "hasValue"));
        if (hasValue is null || TryReadScalar(Dereference(hasValue)) is not { } flag)
            return null;

        if (!string.Equals(flag, bool.TrueString, StringComparison.OrdinalIgnoreCase))
            return "null";

        return Safe(() => FieldValue(value, "value")) is { } inner
            ? DescribeValue(inner)
            : null;
    }

    /// <summary>
    /// An enum is its member's name — <c>Wednesday</c>, or <c>Sweet | Salty</c> for a flags
    /// combination — and only a value no member accounts for shows as a number.
    /// </summary>
    private static string? EnumDisplayOf(CorDebugValue value)
    {
        foreach (var (_, metadata, typeDef) in TypeChain(value))
        {
            if (!ExtendsSystemEnum(metadata, typeDef))
                return null;

            if (RawIntegerOf(Safe(() => FieldValue(value, "value__"))) is not { } underlying)
                return null;

            var literals = new List<(string Name, long Value)>();
            foreach (var field in Fields(metadata, typeDef))
            {
                var props = Safe<GetFieldPropsResult?>(() => metadata.GetFieldProps(field));
                if (props is null || !props.Value.pdwAttr.HasFlag(CorFieldAttr.fdLiteral))
                    continue;
                if (ConstantOf(props.Value) is { } constant)
                    literals.Add((props.Value.szField, constant));
            }

            foreach (var (name, constant) in literals)
            {
                if (constant == underlying)
                    return name;
            }

            // No single member matches: a [Flags] combination, when the declared members cover
            // every set bit between them.
            if (underlying != 0)
            {
                var parts = new List<string>();
                var remaining = underlying;
                foreach (var (name, constant) in literals)
                {
                    if (constant != 0 && (remaining & constant) == constant)
                    {
                        parts.Add(name);
                        remaining &= ~constant;
                    }
                }
                if (remaining == 0 && parts.Count > 0)
                    return string.Join(" | ", parts);
            }

            return underlying.ToString();
        }
        return null;
    }

    private static bool ExtendsSystemEnum(MetaDataImport metadata, mdTypeDef typeDef) =>
        Safe(() =>
        {
            var extends = metadata.GetTypeDefProps(typeDef).ptkExtends;
            return extends.Type switch
            {
                CorTokenType.mdtTypeRef => metadata.GetTypeRefProps((mdTypeRef)extends).szName == "System.Enum",
                CorTokenType.mdtTypeDef => metadata.GetTypeDefProps((mdTypeDef)extends).szTypeDef == "System.Enum",
                _ => false,
            };
        }) == true;

    /// <summary>A literal field's constant, zero-extended so it compares against
    /// <see cref="RawIntegerOf"/> of the same width.</summary>
    private static long? ConstantOf(GetFieldPropsResult props)
    {
        var pointer = props.ppValue;
        if (pointer == IntPtr.Zero)
            return null;

        return props.pdwCPlusTypeFlag switch
        {
            CorElementType.Boolean or CorElementType.I1 or CorElementType.U1 => Marshal.ReadByte(pointer),
            CorElementType.Char or CorElementType.I2 or CorElementType.U2 => (ushort)Marshal.ReadInt16(pointer),
            CorElementType.I4 or CorElementType.U4 => (uint)Marshal.ReadInt32(pointer),
            CorElementType.I8 or CorElementType.U8 => Marshal.ReadInt64(pointer),
            _ => null,
        };
    }

    /// <summary>A value's raw bits as an integer, zero-extended from its own width.</summary>
    private static long? RawIntegerOf(CorDebugValue? value)
    {
        if (value is null || Safe(() => Dereference(value)) is not { } target)
            return null;

        try
        {
            var generic = Extensions.As<CorDebugGenericValue>(target);
            var size = target.Size;
            var pointer = Marshal.AllocHGlobal(Math.Max(size, 8));
            try
            {
                Marshal.WriteInt64(pointer, 0, 0);
                generic.GetValue(pointer);
                return size switch
                {
                    1 => Marshal.ReadByte(pointer),
                    2 => (ushort)Marshal.ReadInt16(pointer),
                    4 => (uint)Marshal.ReadInt32(pointer),
                    _ => Marshal.ReadInt64(pointer),
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

    /// <summary>
    /// The framework structs whose value lives in private fields the runtime cannot read as a
    /// scalar: decimal, DateTime, TimeSpan and Guid all reconstruct from their .NET Framework
    /// field layout, with no function evaluation.
    /// </summary>
    private string? WellKnownStructDisplayOf(CorDebugValue value)
    {
        try
        {
            return TypeNameOf(value) switch
            {
                "System.Decimal" => DecimalDisplayOf(value),
                "System.DateTime" => DateTimeDisplayOf(value),
                "System.TimeSpan" => TimeSpanDisplayOf(value),
                "System.Guid" => GuidDisplayOf(value),
                _ => null,
            };
        }
        catch
        {
            // A layout this reconstruction does not know (or corrupt bits) falls back to the
            // type name rather than showing an invented value.
            return null;
        }
    }

    private static string? DecimalDisplayOf(CorDebugValue value)
    {
        if (RawIntegerOf(FieldValue(value, "flags")) is not { } flags ||
            RawIntegerOf(FieldValue(value, "hi")) is not { } hi ||
            RawIntegerOf(FieldValue(value, "lo")) is not { } lo ||
            RawIntegerOf(FieldValue(value, "mid")) is not { } mid)
            return null;

        var scale = (byte)((flags >> 16) & 0xFF);
        var negative = (flags & 0x8000_0000L) != 0;
        return new decimal((int)(uint)lo, (int)(uint)mid, (int)(uint)hi, negative, scale)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? DateTimeDisplayOf(CorDebugValue value)
    {
        if (RawIntegerOf(FieldValue(value, "dateData")) is not { } data)
            return null;

        var ticks = data & 0x3FFF_FFFF_FFFF_FFFF;
        return ticks <= DateTime.MaxValue.Ticks
            ? new DateTime(ticks).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
    }

    private static string? TimeSpanDisplayOf(CorDebugValue value) =>
        RawIntegerOf(FieldValue(value, "_ticks")) is { } ticks
            ? TimeSpan.FromTicks(ticks).ToString()
            : null;

    private static string? GuidDisplayOf(CorDebugValue value)
    {
        var parts = new long[11];
        var names = new[] { "_a", "_b", "_c", "_d", "_e", "_f", "_g", "_h", "_i", "_j", "_k" };
        for (var i = 0; i < names.Length; i++)
        {
            if (RawIntegerOf(FieldValue(value, names[i])) is not { } part)
                return null;
            parts[i] = part;
        }

        return new Guid(
            (int)(uint)parts[0], (short)(ushort)parts[1], (short)(ushort)parts[2],
            (byte)parts[3], (byte)parts[4], (byte)parts[5], (byte)parts[6],
            (byte)parts[7], (byte)parts[8], (byte)parts[9], (byte)parts[10]).ToString();
    }

    /// <summary>
    /// A value whose type overrides <c>ToString</c> shows what that override says — VS's "call
    /// string-conversion function". A default inherited from a framework root does not count:
    /// <c>Object.ToString</c> prints the type name, which the fallback already shows for free.
    /// </summary>
    private string? ToStringDisplayOf(CorDebugValue value)
    {
        if (!_display.CallToString || _displayDepth >= MaxDisplayDepth)
            return null;
        if (FindMethod(value, "ToString") is not { } found || DeclaredOnFrameworkRoot(found.Function))
            return null;

        _displayDepth++;
        try
        {
            var result = InvokeFunction(found.Function, [found.Instance], out _);
            if (result is null)
                return null;

            return Safe(() => Dereference(result)) is CorDebugStringValue str
                ? Safe(() => str.GetString(str.Length))
                : null;
        }
        finally
        {
            _displayDepth--;
        }
    }

    /// <summary>How much of a string the one-line value shows. A 4 MB payload does not belong in
    /// a value column — VS caps its summaries the same way.</summary>
    private const int MaxStringDisplayLength = 1024;

    /// <summary>A string literal the way VS writes one: quoted, control characters escaped, and
    /// an ellipsis when the value goes on past the cap.</summary>
    private static string QuoteString(string text, bool truncated)
    {
        var builder = new System.Text.StringBuilder(text.Length + 8);
        builder.Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\r': builder.Append("\\r"); break;
                case '\n': builder.Append("\\n"); break;
                case '\t': builder.Append("\\t"); break;
                case '\0': builder.Append("\\0"); break;
                default: builder.Append(c); break;
            }
        }
        if (truncated)
            builder.Append("...");
        builder.Append('"');
        return builder.ToString();
    }

    private static CorElementType? PrimitiveElementTypeOf(string typeName) => typeName switch
    {
        "System.Boolean" => CorElementType.Boolean,
        "System.Char" => CorElementType.Char,
        "System.SByte" => CorElementType.I1,
        "System.Byte" => CorElementType.U1,
        "System.Int16" => CorElementType.I2,
        "System.UInt16" => CorElementType.U2,
        "System.Int32" => CorElementType.I4,
        "System.UInt32" => CorElementType.U4,
        "System.Int64" => CorElementType.I8,
        "System.UInt64" => CorElementType.U8,
        "System.Single" => CorElementType.R4,
        "System.Double" => CorElementType.R8,
        "System.IntPtr" => CorElementType.I,
        "System.UIntPtr" => CorElementType.U,
        // The C#-spelled names too: type naming renders "int", and a boxed primitive resolves
        // its element type through that same rendering.
        "bool" => CorElementType.Boolean,
        "char" => CorElementType.Char,
        "sbyte" => CorElementType.I1,
        "byte" => CorElementType.U1,
        "short" => CorElementType.I2,
        "ushort" => CorElementType.U2,
        "int" => CorElementType.I4,
        "uint" => CorElementType.U4,
        "long" => CorElementType.I8,
        "ulong" => CorElementType.U8,
        "float" => CorElementType.R4,
        "double" => CorElementType.R8,
        _ => null,
    };

    /// <summary>Whether a found method is declared on one of the framework's root types, whose
    /// <c>ToString</c> defaults say nothing a type name does not.</summary>
    private static bool DeclaredOnFrameworkRoot(CorDebugFunction function) =>
        Safe(() =>
        {
            var metadata = Extensions.GetMetaDataInterface<MetaDataImport>(function.Module);
            return metadata.GetTypeDefProps(function.Class.Token).szTypeDef;
        }) is null or "System.Object" or "System.ValueType" or "System.Enum" or "System.Exception"
            or "System.Delegate" or "System.MulticastDelegate" or "System.Type";

    private static string? TryReadScalar(CorDebugValue value)
    {
        try
        {
            var generic = Extensions.As<CorDebugGenericValue>(value);
            var pointer = Marshal.AllocHGlobal(Math.Max(value.Size, 8));
            try
            {
                generic.GetValue(pointer);
                // The exact type sees through an unboxed primitive, whose plain Type is the box's
                // class rather than the element type of what it holds; when even that reports a
                // class, the metadata name still identifies a primitive.
                var type = Safe(() => (CorElementType?)value.ExactType?.Type) ?? value.Type;
                if (type is CorElementType.ValueType or CorElementType.Class)
                    type = PrimitiveElementTypeOf(TypeNameOf(value)) ?? type;
                // Invariant culture throughout: a value read out on a Dutch host must parse back
                // in on any other, and VS shows "Infinity", not "∞".
                var invariant = System.Globalization.CultureInfo.InvariantCulture;
                return type switch
                {
                    CorElementType.Boolean => (Marshal.ReadByte(pointer) != 0).ToString(),
                    CorElementType.Char => $"'{(char)Marshal.ReadInt16(pointer)}'",
                    CorElementType.I1 => ((sbyte)Marshal.ReadByte(pointer)).ToString(invariant),
                    CorElementType.U1 => Marshal.ReadByte(pointer).ToString(invariant),
                    CorElementType.I2 => Marshal.ReadInt16(pointer).ToString(invariant),
                    CorElementType.U2 => ((ushort)Marshal.ReadInt16(pointer)).ToString(invariant),
                    CorElementType.I4 => Marshal.ReadInt32(pointer).ToString(invariant),
                    CorElementType.U4 => ((uint)Marshal.ReadInt32(pointer)).ToString(invariant),
                    CorElementType.I8 => Marshal.ReadInt64(pointer).ToString(invariant),
                    CorElementType.U8 => ((ulong)Marshal.ReadInt64(pointer)).ToString(invariant),
                    CorElementType.R4 => BitConverter.Int32BitsToSingle(Marshal.ReadInt32(pointer)).ToString(invariant),
                    CorElementType.R8 => BitConverter.Int64BitsToDouble(Marshal.ReadInt64(pointer)).ToString(invariant),
                    // Native-sized values — IntPtr, handles — read as VS shows them: an address.
                    CorElementType.I or CorElementType.U or CorElementType.Ptr or CorElementType.FnPtr =>
                        value.Size == 8 ? $"0x{Marshal.ReadInt64(pointer):x16}" : $"0x{Marshal.ReadInt32(pointer):x8}",
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
            ProcessId = Pid,
        });

    /// <summary>Runs a call whose failure changes nothing — a cleanup, or a hint the runtime is
    /// free to refuse.</summary>
    private static void Try(Action action)
    {
        try { action(); }
        catch { }
    }

    /// <summary>How the debuggee's run ended, naming the cause when the system killed it.</summary>
    /// <remarks>
    /// "process exited" on its own cannot tell a program that finished from one the operating
    /// system tore down, and the loudest of those cannot report itself: a stack overflow leaves
    /// the runtime no stack to raise an exception on, so it writes a single line to the debuggee's
    /// own stderr and dies. A session that relays only managed events shows that as a clean exit,
    /// and the user is left with a debugger that stopped for no stated reason. The exit code is
    /// the durable record of it, and it can only be read through a handle taken while the process
    /// was still alive — which is what <see cref="Pid"/>'s setter is for.
    /// </remarks>
    private string DescribeExit()
    {
        int? code = null;
        try
        {
            if (_debuggee is { HasExited: true } debuggee)
                code = debuggee.ExitCode;
        }
        catch
        {
            // No handle, no rights to it, or the exit has not settled. An unnamed exit is still
            // an exit, and guessing at a cause would be worse than not naming one.
        }

        if (code is not { } value || value == 0)
            return "process exited";

        uint status = unchecked((uint)value);
        return FatalExitName(status) is { } named
            ? $"process exited: {named} (0x{status:X8})"
            : $"process exited with code {value} (0x{status:X8})";
    }

    /// <summary>
    /// The exit codes worth naming: the ones that mean the debuggee was killed rather than
    /// finished, and that point at something the user can do something about.
    /// </summary>
    /// <remarks>
    /// Deliberately a short list. A code this does not recognise is still reported, in hex, which
    /// is enough to look up — inventing a plausible name for an unknown status would make the
    /// report less trustworthy, not more.
    /// </remarks>
    public static string? FatalExitName(uint code) => code switch
    {
        0xC00000FD => "stack overflow",
        0xC0000005 => "access violation",
        0xC0000374 => "heap corruption",
        0xC000013A => "interrupted",
        0xE0434352 => "an unhandled managed exception",
        0x80131506 => "an execution engine error",
        _ => null,
    };

    private static T? Safe<T>(Func<T> f)
    {
        try { return f(); }
        catch { return default; }
    }
}

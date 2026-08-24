using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace RoslynMCP.Services.Debugging;

/// <summary>
/// A Debug Adapter Protocol server over stdio, backed by an <see cref="IDebugBackend"/>.
/// </summary>
/// <remarks>
/// This exists for .NET Framework. netcoredbg speaks DAP natively but debugs CoreCLR only, and
/// ICorDebug — the only way onto the desktop runtime — has no adapter at all, so a Framework
/// project could be debugged by the AI and not by the user. Since T3.1 the backend already
/// answers with frames, variables, and threads; the remaining distance to DAP is framing and
/// naming, which is what this is.
/// </remarks>
internal sealed class DapServer
{
    private readonly IDebugBackend _backend;
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private int _sequence;
    /// <summary>Written by request handlers, read by the listen loop — different threads.</summary>
    private volatile bool _running = true;

    /// <summary>
    /// How long a debuggee gets to shut itself down before it is killed.
    /// </summary>
    /// <remarks>
    /// Long enough for a host to drain its services (the generic host's own default shutdown
    /// budget is 30 seconds, but one that needs all of it is hanging, not shutting down) and
    /// short enough that a stop press does not look ignored.
    /// </remarks>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Set once the session is over, so the end is announced exactly once.</summary>
    private int _ended;

    /// <summary>Whether this adapter started the debuggee, as opposed to attaching to one that
    /// was already running — which decides who owns its lifetime at disconnect.</summary>
    private volatile bool _launched;

    /// <summary>The file the last <c>gotoTargets</c> asked about. DAP's <c>goto</c> carries only a
    /// target id, so the source has to be remembered from the request that produced it.</summary>
    private string? _gotoSource;

    /// <summary>What the client currently has set per source, as line -> engine breakpoint.
    /// <c>setBreakpoints</c> replaces a source's whole set, so the previous one has to be known
    /// to tell an unchanged breakpoint from a new one and to remove the dropped ones.</summary>
    private readonly Dictionary<string, Dictionary<int, TrackedBreakpoint>> _sourceBreakpoints =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _breakpointLock = new(1, 1);

    /// <summary>Engine breakpoint id -> the line it actually bound at, under its own lock: the
    /// notice pump must never queue behind a <c>setBreakpoints</c>, or a stop would reach the
    /// client only once the breakpoint call it was stuck behind returned.</summary>
    private readonly Dictionary<int, int> _boundLines = [];

    /// <summary>How many resuming commands are in flight. A stop that ends one of them is
    /// reported by that command; only a stop with none outstanding is the adapter's to announce.</summary>
    private int _resuming;

    /// <summary>The engine's own account of what it is doing, when it has one. Without it, "the
    /// engine accepted it" is the only answer available about a breakpoint, and a resume that
    /// returns without a stop cannot be told from a debuggee that ended.</summary>
    private readonly IDebugNoticeSource? _engine;

    /// <summary>The last stop announced to the client. A stop arrives twice — as the result of the
    /// command that resumed into it and as a notice — so it is claimed once and skipped after.</summary>
    private long _reportedStop;

    /// <summary>Engine notices, drained in order by one writer. Fire-and-forget writes from the
    /// engine's pump thread would interleave with each other and with stop events.</summary>
    private readonly Channel<DebugNotice> _notices = Channel.CreateUnbounded<DebugNotice>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _noticePump;

    /// <summary>Completed once launch or attach has been answered. Requests are dispatched
    /// concurrently, so configuration requests arrive while the launch is still in flight;
    /// answering them against a session that does not exist yet drops the user's F5
    /// breakpoints on the floor with no retry.</summary>
    private readonly TaskCompletionSource _sessionStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record TrackedBreakpoint(int Id, string? Condition, string? HitCondition, string? LogMessage);

    /// <summary>DAP frame ids are opaque; the backend's are stack indices, so scopes carry the
    /// frame in their reference and variable references pass through untouched.</summary>
    internal const int ScopeBase = 1;
    internal const int ScopeLimit = 1000;

    public DapServer(IDebugBackend backend, Stream input, Stream output)
    {
        _backend = backend;
        _input = input;
        _output = output;

        // Subscribed before anything can launch, so the module loads and bind results from
        // startup — the ones that explain a breakpoint that never binds — are not missed.
        if (backend is IDebugNoticeSource source)
        {
            _engine = source;
            source.Notice += notice => _notices.Writer.TryWrite(notice);
        }

        _noticePump = Task.Run(PumpNoticesAsync);
    }

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        using var backend = new PublishingDebugBackend(new IcorDebugBackend());

        // The same command pipe the AI-owned sessions expose. Hot reload needs it: the delta is
        // computed in the daemon, where the workspace lives, but only this process can apply it.
        using var commands = new DebugCommandPipeServer(() => backend);

        // Not Console.OpenStandardOutput(): it reports short pipe writes as successful, which
        // drops bytes out of a DAP frame and desynchronizes the editor's adapter. See StdIo.
        var input = Console.OpenStandardInput();
        var output = StdIo.OpenProtocolOutput();

        // stdout belongs to the protocol from here on, so nothing may reach it by another route:
        // one stray Console.WriteLine anywhere under the debugger would land inside a frame.
        Console.SetOut(Console.Error);

        var server = new DapServer(backend, input, output);

        await server.ListenAsync(ct);
        return 0;
    }

    public async Task ListenAsync(CancellationToken ct)
    {
        try
        {
            while (_running && !ct.IsCancellationRequested)
            {
                JsonNode? message;
                try
                {
                    message = await ReadMessageAsync(ct);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                {
                    return;
                }
                if (message is null)
                    return;

                // Requests are handled concurrently: a blocking `continue` must not stop the client
                // from cancelling it or asking for state.
                _ = HandleAsync(message, ct);
            }
        }
        finally
        {
            // Nothing can start a session any more, so whatever is still waiting for one is
            // released to fail rather than to hang.
            _sessionStarted.TrySetResult();

            // Flush what the engine already said before the transport goes away, but do not wait
            // on a handler that is wedged — the session is ending either way.
            _notices.Writer.TryComplete();
            await Task.WhenAny(_noticePump, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
        }
    }

    private async Task HandleAsync(JsonNode message, CancellationToken ct)
    {
        string command = message["command"]?.GetValue<string>() ?? "";
        var arguments = message["arguments"];

        try
        {
            switch (command)
            {
                case "initialize":
                    await RespondAsync(message, Capabilities());
                    await EventAsync("initialized", null);
                    break;

                case "launch":
                {
                    string result = await LaunchAsync(arguments, ct);
                    bool ok = !result.StartsWith("Error", StringComparison.OrdinalIgnoreCase);
                    _sessionStarted.TrySetResult();
                    await RespondAsync(message, null, ok, ok ? null : result);
                    if (ok)
                    {
                        _launched = true;
                        await ProcessEventAsync(arguments, startMethod: "launch");
                    }
                    else
                        await EventAsync("terminated", null);
                    break;
                }

                case "attach":
                {
                    int pid = arguments?["processId"]?.GetValue<int>() ?? 0;
                    if (pid <= 0)
                    {
                        _sessionStarted.TrySetResult();
                        await RespondAsync(message, null, false, "No process id to attach to.");
                        break;
                    }
                    string result = await _backend.AttachToProcessAsync(pid, null, ct);
                    bool ok = !result.StartsWith("Error", StringComparison.OrdinalIgnoreCase);
                    _sessionStarted.TrySetResult();
                    await RespondAsync(message, null, ok, ok ? null : result);
                    if (ok)
                        await ProcessEventAsync(arguments, startMethod: "attach");
                    else
                        await EventAsync("terminated", null);
                    break;
                }

                case "configurationDone":
                    // The client has finished configuring; if no launch or attach ever came, the
                    // queued configuration requests would otherwise wait forever.
                    _sessionStarted.TrySetResult();
                    await RespondAsync(message, null);
                    break;

                case "setBreakpoints":
                {
                    await _sessionStarted.Task;
                    await ApplyBreakpointsAsync(message, arguments, ct);
                    break;
                }

                case "dataBreakpointInfo":
                {
                    // The client asks this about a name it saw in the Variables view; the frame is
                    // part of the id so the same name in two frames is two watches.
                    string name = arguments?["name"]?.GetValue<string>() ?? "";
                    int frameId = arguments?["frameId"]?.GetValue<int>() ?? 0;

                    if (name.Length == 0 || _backend.CurrentFrame is null)
                    {
                        await RespondAsync(message, new JsonObject
                        {
                            ["dataId"] = null,
                            ["description"] = "Break on value change needs a suspended target and a named value.",
                        });
                        break;
                    }

                    await RespondAsync(message, new JsonObject
                    {
                        ["dataId"] = DataBreakpointId.For(name, frameId),
                        ["description"] = $"{name} (break when the value changes)",
                        // Reads are not detectable by comparing values, so they are not offered.
                        ["accessTypes"] = new JsonArray { "write" },
                        ["canPersist"] = false,
                    });
                    break;
                }

                case "setDataBreakpoints":
                {
                    await _sessionStarted.Task;
                    if (_backend is not PublishingDebugBackend watching)
                    {
                        await RespondAsync(message, null, false, "This adapter cannot watch values.");
                        break;
                    }

                    var requested = arguments?["breakpoints"]?.AsArray() ?? [];
                    var specs = new List<DataBreakpointSpec>();
                    // DAP wants one answer per requested breakpoint, so an entry with no data id
                    // has to occupy its slot rather than shift every later answer up one.
                    var armed = new List<bool>(requested.Count);
                    foreach (var entry in requested)
                    {
                        string dataId = entry?["dataId"]?.GetValue<string>() ?? "";
                        armed.Add(dataId.Length > 0);
                        if (dataId.Length == 0)
                            continue;

                        specs.Add(new DataBreakpointSpec(
                            dataId,
                            DataBreakpointId.ExpressionOf(dataId),
                            entry?["accessType"]?.GetValue<string>() ?? "write",
                            entry?["condition"]?.GetValue<string>(),
                            entry?["hitCondition"]?.GetValue<string>()));
                    }

                    var statuses = await watching.SetDataBreakpointsAsync(specs, ct);
                    var verified = new JsonArray();
                    int next = 0;
                    foreach (bool hasId in armed)
                    {
                        if (!hasId)
                        {
                            verified.Add(new JsonObject
                            {
                                ["verified"] = false,
                                ["message"] = "The breakpoint carried no data id.",
                            });
                            continue;
                        }

                        var status = next < statuses.Count ? statuses[next++] : null;
                        var result = new JsonObject { ["verified"] = status?.Verified ?? false };
                        if (status is null || !status.Verified)
                            result["message"] = status?.Message ?? "The value could not be watched.";
                        verified.Add(result);
                    }

                    await RespondAsync(message, new JsonObject { ["breakpoints"] = verified });
                    break;
                }

                case "gotoTargets":
                {
                    // DAP asks which lines are legal before offering the jump. The engine decides
                    // that when the move is attempted, so the requested line is offered and a
                    // refusal comes back from `goto` rather than being predicted here.
                    string? file = arguments?["source"]?["path"]?.GetValue<string>();
                    int line = arguments?["line"]?.GetValue<int>() ?? 0;

                    await RespondAsync(message, new JsonObject
                    {
                        ["targets"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["id"] = line,
                                ["label"] = $"{Path.GetFileName(file ?? "")}:{line}",
                                ["line"] = line,
                            },
                        },
                    });
                    _gotoSource = file;
                    break;
                }

                case "goto":
                {
                    int line = arguments?["targetId"]?.GetValue<int>() ?? 0;
                    string result = await _backend.SetNextStatementAsync(_gotoSource ?? "", line, ct);
                    bool ok = !result.StartsWith("Error", StringComparison.OrdinalIgnoreCase);

                    await RespondAsync(message, null, ok, ok ? null : result);
                    if (ok)
                        await ReportStopAsync("goto");
                    break;
                }

                case "setExceptionBreakpoints":
                {
                    await _sessionStarted.Task;
                    var ids = (arguments?["filters"]?.AsArray() ?? [])
                        .Select(f => f?.GetValue<string>() ?? "")
                        .ToList();
                    await _backend.SetExceptionFiltersAsync(ExceptionFilters.FromIds(ids), ct);
                    await RespondAsync(message, null);
                    break;
                }

                case "threads":
                {
                    var threads = await _backend.GetThreadsAsync(ct);
                    var array = new JsonArray();
                    foreach (var thread in threads)
                        array.Add(new JsonObject { ["id"] = thread.Id, ["name"] = thread.Name });
                    if (array.Count == 0)
                        array.Add(new JsonObject { ["id"] = 1, ["name"] = "Main Thread" });

                    await RespondAsync(message, new JsonObject { ["threads"] = array });
                    break;
                }

                case "stackTrace":
                {
                    var frames = await _backend.GetStackFramesAsync(ct);
                    var array = new JsonArray();
                    foreach (var frame in frames)
                    {
                        var entry = new JsonObject
                        {
                            ["id"] = frame.Id,
                            ["name"] = frame.Name,
                            ["line"] = frame.Line,
                            ["column"] = frame.Column == 0 ? 1 : frame.Column,
                        };
                        if (frame.FilePath.Length > 0)
                        {
                            var source = new JsonObject
                            {
                                ["name"] = Path.GetFileName(frame.FilePath),
                                ["path"] = frame.FilePath,
                            };
                            // Resolved external source — say where it came from, the way VS
                            // labels decompiled frames.
                            if (frame.SourceOrigin.Length > 0)
                                source["origin"] = frame.SourceOrigin;
                            entry["source"] = source;
                        }
                        if (frame.IsExternal)
                            entry["presentationHint"] = "subtle";
                        array.Add(entry);
                    }

                    await RespondAsync(message, new JsonObject
                    {
                        ["stackFrames"] = array,
                        ["totalFrames"] = array.Count,
                    });
                    break;
                }

                case "scopes":
                {
                    int frameId = arguments?["frameId"]?.GetValue<int>() ?? 0;
                    await RespondAsync(message, new JsonObject
                    {
                        ["scopes"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["name"] = "Locals",
                                ["variablesReference"] = ScopeBase + frameId,
                                ["expensive"] = false,
                            },
                        },
                    });
                    break;
                }

                case "variables":
                {
                    int reference = arguments?["variablesReference"]?.GetValue<int>() ?? ScopeBase;
                    var variables = reference >= ScopeBase && reference < ScopeBase + ScopeLimit
                        ? await _backend.GetVariablesAsync(reference - ScopeBase, ct)
                        : await _backend.GetVariableChildrenAsync(reference, ct);

                    var array = new JsonArray();
                    foreach (var variable in variables)
                    {
                        var entry = new JsonObject
                        {
                            ["name"] = variable.Name,
                            ["value"] = variable.Value,
                            ["variablesReference"] = variable.VariablesReference,
                            ["evaluateName"] = variable.Name,
                        };
                        if (variable.Type.Length > 0)
                            entry["type"] = variable.Type;
                        if (variable.NamedChildCount > 0)
                            entry["namedVariables"] = variable.NamedChildCount;
                        if (variable.IndexedChildCount > 0)
                            entry["indexedVariables"] = variable.IndexedChildCount;
                        array.Add(entry);
                    }
                    await RespondAsync(message, new JsonObject { ["variables"] = array });
                    break;
                }

                case "setVariable":
                {
                    // The reference names the scope being edited, and a scope's reference carries
                    // its frame — writing frame 0 regardless edits the wrong frame's local.
                    int reference = arguments?["variablesReference"]?.GetValue<int>() ?? ScopeBase;
                    int frameId = reference >= ScopeBase && reference < ScopeBase + ScopeLimit
                        ? reference - ScopeBase
                        : 0;

                    var (ok, stored, error) = await _backend.SetVariableAsync(
                        arguments?["name"]?.GetValue<string>() ?? "",
                        arguments?["value"]?.GetValue<string>() ?? "",
                        frameId, ct);

                    await RespondAsync(
                        message,
                        ok ? new JsonObject { ["value"] = stored } : null,
                        ok, ok ? null : error);
                    break;
                }

                case "evaluate":
                {
                    string result = await _backend.EvaluateAsync(
                        arguments?["expression"]?.GetValue<string>() ?? "", ct);
                    bool ok = !result.StartsWith("Error", StringComparison.OrdinalIgnoreCase);
                    await RespondAsync(
                        message,
                        new JsonObject { ["result"] = result, ["variablesReference"] = 0 },
                        ok, ok ? null : result);
                    break;
                }

                case "continue":
                case "next":
                case "stepIn":
                case "stepOut":
                {
                    await EventAsync("continued", new JsonObject
                    {
                        ["threadId"] = 1,
                        ["allThreadsContinued"] = true,
                    });

                    Interlocked.Increment(ref _resuming);
                    try
                    {
                        var resume = command switch
                        {
                            "next" => _backend.StepOverAsync(ct),
                            "stepIn" => _backend.StepInAsync(ct),
                            "stepOut" => _backend.StepOutAsync(ct),
                            _ => _backend.ContinueAsync(ct),
                        };
                        await resume;

                        await RespondAsync(
                            message,
                            command == "continue" ? new JsonObject { ["allThreadsContinued"] = true } : null);
                        await ReportStopAsync(command == "continue" ? "breakpoint" : "step");
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _resuming);
                    }
                    break;
                }

                case "pause":
                {
                    Interlocked.Increment(ref _resuming);
                    try
                    {
                        string result = await _backend.InterruptAsync(ct);
                        bool ok = !result.StartsWith("Error", StringComparison.OrdinalIgnoreCase);
                        await RespondAsync(message, null, ok, ok ? null : result);
                        if (ok)
                            await ReportStopAsync("pause");
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _resuming);
                    }
                    break;
                }

                case "exceptionInfo":
                {
                    var detail = await _backend.GetExceptionInfoAsync(ct);
                    if (detail is null)
                    {
                        await RespondAsync(message, null, false, "The target did not stop on an exception.");
                        break;
                    }
                    await RespondAsync(message, new JsonObject
                    {
                        ["exceptionId"] = detail.TypeName,
                        ["description"] = detail.Message,
                        ["breakMode"] = detail.BreakMode,
                    });
                    break;
                }

                case "terminate":
                    // Answered before the shutdown starts, not after it: the editor gives this
                    // request its own deadline and force-disconnects when it passes, and a clean
                    // shutdown is exactly the case that takes seconds rather than milliseconds.
                    await RespondAsync(message, null);
                    _ = ShutdownAsync(ct);
                    break;

                case "disconnect":
                {
                    // Whether the debuggee dies with the session. The editor sets it explicitly
                    // for Disconnect; when it says nothing, a process this adapter started is
                    // ours to stop and one it merely attached to is not.
                    bool terminateDebuggee = arguments?["terminateDebuggee"]?.GetValue<bool>()
                        ?? _launched;

                    if (terminateDebuggee)
                    {
                        // A disconnect that follows the terminate above is the editor's force
                        // path — the debuggee has outstayed its welcome and is killed outright.
                        _backend.Stop();
                    }
                    else
                    {
                        await _backend.DetachAsync(ct);
                    }

                    await RespondAsync(message, null);
                    await EndSessionAsync();
                    _running = false;
                    break;
                }

                default:
                    await RespondAsync(message, null, false, $"Unsupported request '{command}'.");
                    break;
            }
        }
        catch (Exception ex)
        {
            await RespondAsync(message, null, false, ex.Message);
        }
    }

    /// <summary>
    /// Applies the client's breakpoints for one source as a replacement of what it had before.
    /// </summary>
    /// <remarks>
    /// <para>
    /// DAP has no "remove breakpoint" request: the client resends a source's whole set on every
    /// change, and what is missing from it is meant to be gone. Adding only, as this did, left
    /// deleted breakpoints armed and turned a resend of an unchanged list into a second engine
    /// breakpoint on every line — so an unchanged breakpoint keeps its id and only genuinely new
    /// or edited ones reach the engine.
    /// </para>
    /// <para>
    /// Binding is asynchronous, so the answer reports what the engine has bound so far and the
    /// rest arrives as <c>breakpoint</c> events. A bind that lands while this is still running is
    /// therefore already in the response, and the event that follows it is a harmless repeat.
    /// </para>
    /// </remarks>
    private async Task ApplyBreakpointsAsync(JsonNode request, JsonNode? arguments, CancellationToken ct)
    {
        string file = arguments?["source"]?["path"]?.GetValue<string>() ?? "";
        var wanted = arguments?["breakpoints"]?.AsArray() ?? [];
        var answered = new JsonArray();

        await _breakpointLock.WaitAsync(ct);
        try
        {
            var previous = _sourceBreakpoints.TryGetValue(file, out var existing)
                ? existing
                : [];
            var current = new Dictionary<int, TrackedBreakpoint>();

            var requested = new List<(int Line, string? Condition, string? HitCondition, string? LogMessage)>();
            foreach (var breakpoint in wanted)
            {
                requested.Add((
                    breakpoint?["line"]?.GetValue<int>() ?? 0,
                    breakpoint?["condition"]?.GetValue<string>(),
                    breakpoint?["hitCondition"]?.GetValue<string>(),
                    breakpoint?["logMessage"]?.GetValue<string>()));
            }

            // Everything that is going is removed before anything new is set. The other order
            // destroys a breakpoint whose condition was edited: the engine keys its breakpoints by
            // file and line, so setting the replacement first cannot bind — the old one still owns
            // that line — and removing the old one then takes the replacement with it, leaving the
            // line with no breakpoint at all and nothing pending to rebind it.
            foreach (var (line, dropped) in previous)
            {
                var kept = requested.FirstOrDefault(r => r.Line == line);
                if (kept.Line == line &&
                    kept.Condition == dropped.Condition &&
                    kept.HitCondition == dropped.HitCondition &&
                    kept.LogMessage == dropped.LogMessage)
                {
                    current[line] = dropped;
                    continue;
                }

                await _backend.RemoveBreakpointAsync(dropped.Id, ct);
                lock (_boundLines)
                    _boundLines.Remove(dropped.Id);
            }

            foreach (var (line, condition, hitCondition, logMessage) in requested)
            {
                if (current.ContainsKey(line))
                {
                    answered.Add(Describe(current[line].Id, line, file));
                    continue;
                }

                var (_, id) = await _backend.SetBreakpointAsync(
                    file, line, condition, hitCondition, logMessage, ct);

                if (id is { } assigned)
                {
                    current[line] = new TrackedBreakpoint(assigned, condition, hitCondition, logMessage);
                    answered.Add(Describe(assigned, line, file));
                }
                else
                {
                    answered.Add(new JsonObject
                    {
                        ["verified"] = false,
                        ["line"] = line,
                        ["message"] = "The engine refused the breakpoint.",
                    });
                }
            }

            _sourceBreakpoints[file] = current;

            await RespondAsync(request, new JsonObject { ["breakpoints"] = answered });
        }
        finally
        {
            _breakpointLock.Release();
        }
    }

    /// <summary>
    /// One breakpoint as the client should draw it: solid at the line it bound to, hollow with a
    /// reason while it is still pending.
    /// </summary>
    /// <remarks>
    /// A breakpoint the engine accepted is not a breakpoint that will be hit — its module may not
    /// have loaded, or may have shipped without a PDB. Reporting acceptance as verification, which
    /// is what this did, drew a solid red dot on a breakpoint that could never fire and left the
    /// user to conclude the debugger was broken.
    /// </remarks>
    private JsonObject Describe(int id, int requestedLine, string file)
    {
        bool bound;
        int boundLine;
        lock (_boundLines)
            bound = _boundLines.TryGetValue(id, out boundLine);

        var entry = new JsonObject
        {
            ["id"] = id,
            ["verified"] = bound || _engine is null,
            ["line"] = bound ? boundLine : requestedLine,
        };

        if (!bound && _engine is not null)
            entry["message"] = "Pending — its module has not loaded, or was built without symbols.";
        if (file.Length > 0)
            entry["source"] = new JsonObject { ["name"] = Path.GetFileName(file), ["path"] = file };

        return entry;
    }

    // --- Engine notices ---

    /// <summary>
    /// Relays what the engine says between stops: the debuggee's console, the engine's own
    /// diagnostics, module loads, and breakpoints binding and unbinding.
    /// </summary>
    /// <remarks>
    /// The engine has reported all of this from the start and nothing consumed it, so a
    /// breakpoint that never bound was indistinguishable from code that never ran — the Debug
    /// Console stayed empty and the gutter dot stayed red.
    /// </remarks>
    private async Task PumpNoticesAsync()
    {
        await foreach (var notice in _notices.Reader.ReadAllAsync())
        {
            try
            {
                await DeliverAsync(notice);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                return; // The client is gone; there is nobody left to tell.
            }
            catch
            {
                // A diagnostic that cannot be formatted must not stop the ones behind it.
            }
        }
    }

    private Task DeliverAsync(DebugNotice notice) => notice.Kind switch
    {
        DebugNoticeKind.Output => OutputAsync("stdout", notice.Message),
        DebugNoticeKind.Diagnostic => OutputAsync("console", notice.Message),
        DebugNoticeKind.Module => OutputAsync("console", $"Loaded '{notice.Message}'."),
        DebugNoticeKind.Stopped => ReportUnaskedStopAsync(notice),
        DebugNoticeKind.Resumed => ReportUnaskedResumeAsync(),
        DebugNoticeKind.Exited => EndSessionAsync(),
        _ => BreakpointChangedAsync(notice),
    };

    /// <summary>
    /// Announces a stop the adapter did not ask for.
    /// </summary>
    /// <remarks>
    /// This is what makes a breakpoint visible at all in a server. <c>stopped</c> used to be sent
    /// only as the tail of a resume this adapter had issued, so after F5 — where nothing is
    /// outstanding — a request that hit a breakpoint suspended the process and the editor was
    /// never told: the site hung with no call stack, no variables, and no highlighted line, which
    /// looks exactly like a breakpoint that does not work.
    /// </remarks>
    private async Task ReportUnaskedStopAsync(DebugNotice notice)
    {
        // A stop that ends a resume is that command's to report, and the emulated hit counts and
        // logpoints resume through stops the user must never see — all of which happen inside a
        // command. Reporting those here would surface them twice, and surface the invisible ones.
        if (Volatile.Read(ref _resuming) > 0)
            return;

        // A logpoint or an unmet hit count is a stop the user must never see, and outside a
        // command nothing else applies that rule — the emulation lives in the resume path.
        if (_backend is PublishingDebugBackend emulating &&
            await emulating.ResumeThroughUnsolicitedStopAsync())
        {
            return;
        }

        await ReportStopAsync(notice.Message.Length > 0 ? notice.Message : "breakpoint");
    }

    /// <summary>
    /// Announces a resume the adapter did not ask for — the chat continuing a session this
    /// editor is attached to over the command pipe. Unsolicited stops were already pushed;
    /// without the matching push here the editor kept showing the old stop until the next one,
    /// and forever if none came.
    /// </summary>
    private async Task ReportUnaskedResumeAsync()
    {
        // A resume this adapter issued is narrated by its own command handler.
        if (Volatile.Read(ref _resuming) > 0)
            return;

        // Stopped again already: that stop's own notice sits behind this one in the queue (or
        // its command already reported it), and a late "continued" would overwrite it.
        if (_backend.CurrentFrame is not null)
            return;

        await EventAsync("continued", new JsonObject
        {
            ["threadId"] = 1,
            ["allThreadsContinued"] = true,
        });
    }

    /// <summary>The debuggee ended on its own — the site was stopped, or it crashed. Without this
    /// the editor keeps a debug session alive against a process that no longer exists.</summary>
    /// <remarks>
    /// Reported once per session, whoever gets there first: a clean shutdown ends the process,
    /// which raises the engine's own exit notice, and both routes arrive here.
    /// </remarks>
    private async Task EndSessionAsync()
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0)
            return;

        await EventAsync("exited", new JsonObject { ["exitCode"] = 0 });
        await EventAsync("terminated", null);
    }

    /// <summary>
    /// Stops the session the way the stop button means it: the debuggee is asked to shut itself
    /// down, and killed only if it will not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The difference this makes is everything a process does on its way out — hosted services'
    /// <c>StopAsync</c>, <c>finally</c> blocks, flushed logs and closed connections. Terminating
    /// the process, which is what stopping a session used to do, skips all of it.
    /// </para>
    /// <para>
    /// The wait is bounded because a shutdown can hang on the very code being debugged. Pressing
    /// stop again meanwhile sends <c>disconnect</c>, which kills the process without waiting for
    /// this — the editor's own escalation, and the reason the timeout can afford to be generous.
    /// </para>
    /// </remarks>
    private async Task ShutdownAsync(CancellationToken ct)
    {
        try
        {
            var (graceful, message) = await _backend.ShutdownAsync(ShutdownTimeout, ct);

            // A clean exit needs no narration; anything else changed what the user asked for and
            // has to say so, or a killed debuggee looks exactly like one that shut down.
            if (!graceful)
                await OutputAsync("console", message);
        }
        catch (Exception ex)
        {
            await OutputAsync("console", $"The debuggee could not be shut down: {ex.Message}");
            _backend.Stop();
        }

        await EndSessionAsync();
        _running = false;
    }

    private Task OutputAsync(string category, string text) =>
        EventAsync("output", new JsonObject
        {
            ["category"] = category,
            ["output"] = text.EndsWith('\n') ? text : text + "\n",
        });

    private async Task BreakpointChangedAsync(DebugNotice notice)
    {
        if (notice.BreakpointId <= 0)
            return;

        bool bound = notice.Kind == DebugNoticeKind.BreakpointBound;

        lock (_boundLines)
        {
            if (bound)
                _boundLines[notice.BreakpointId] = notice.Line;
            else
                _boundLines.Remove(notice.BreakpointId);
        }

        var breakpoint = new JsonObject
        {
            ["id"] = notice.BreakpointId,
            ["verified"] = bound,
            ["message"] = notice.Message,
        };
        if (notice.Line > 0)
            breakpoint["line"] = notice.Line;
        if (notice.FilePath.Length > 0)
        {
            breakpoint["source"] = new JsonObject
            {
                ["name"] = Path.GetFileName(notice.FilePath),
                ["path"] = notice.FilePath,
            };
        }

        await EventAsync("breakpoint", new JsonObject
        {
            ["reason"] = "changed",
            ["breakpoint"] = breakpoint,
        });
    }

    /// <summary>
    /// Resolves the project the client named into a concrete launch and starts it suspended.
    /// </summary>
    /// <remarks>
    /// The configuration carries a project rather than an executable, so the same resolution the
    /// Run command uses applies here — including IIS Express for a legacy web project, which is
    /// launched and then attached to, since the site's code runs in the worker rather than in the
    /// process the client asked to start.
    /// </remarks>
    private async Task<string> LaunchAsync(JsonNode? arguments, CancellationToken ct)
    {
        string? projectPath = arguments?["projectPath"]?.GetValue<string>();
        string? program = arguments?["program"]?.GetValue<string>();

        // Through the decorator, not around it: a launched session has to reach DebugStateStore
        // like any other, or the AI's debug tools cannot see what the user is debugging.
        if (_backend is not PublishingDebugBackend publishing)
            return "Error: this adapter only debugs .NET Framework targets.";

        if (projectPath is { Length: > 0 })
        {
            var spec = Run.RunConfigResolver.Resolve(
                projectPath,
                arguments?["configuration"]?.GetValue<string>() ?? "Debug");

            if (!spec.CanRun)
                return $"Error: {spec.Error}";

            string result = await publishing.LaunchAsync(
                spec.Executable,
                spec.Arguments,
                spec.Environment,
                spec.WorkingDirectory,
                initialBreakpoints: null,
                ct);

            if (!result.StartsWith("Error", StringComparison.OrdinalIgnoreCase) &&
                spec.Port is { } port && spec.Url is { Length: > 0 } url)
            {
                _ = AnnounceWhenListeningAsync(port, url, ct);
            }

            return result;
        }

        if (program is { Length: > 0 })
        {
            var environment = new Dictionary<string, string>();
            foreach (var entry in arguments?["env"]?.AsObject() ?? [])
                environment[entry.Key] = entry.Value?.GetValue<string>() ?? "";

            var argumentList = (arguments?["args"]?.AsArray() ?? [])
                .Select(a => a?.GetValue<string>() ?? "")
                .ToList();

            return await publishing.LaunchAsync(
                program, argumentList, environment,
                arguments?["cwd"]?.GetValue<string>(),
                initialBreakpoints: null, ct);
        }

        return "Error: the launch configuration named neither a project nor a program.";
    }

    /// <summary>
    /// Waits for the site's port to accept a connection, then says so in the wording the client
    /// watches for.
    /// </summary>
    /// <remarks>
    /// This is what makes F5 on a classic ASP.NET site behave like F5 on anything else. IIS
    /// Express prints nothing a "server ready" rule can match, and — more importantly — none of
    /// the site's own code runs until a request arrives, so without opening the browser the
    /// session looks like it launched and died. The wording matches Kestrel's because the client
    /// already watches for it.
    /// </remarks>
    private async Task AnnounceWhenListeningAsync(int port, string url, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                await client.ConnectAsync("127.0.0.1", port, ct);

                await EventAsync("output", new JsonObject
                {
                    ["category"] = "console",
                    ["output"] = $"Now listening on: {url}\n",
                });
                return;
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException)
            {
                if (ct.IsCancellationRequested)
                    return;
                await Task.Delay(200, CancellationToken.None);
            }
        }

        await EventAsync("output", new JsonObject
        {
            ["category"] = "stderr",
            ["output"] = $"The site did not start listening on port {port}.\n",
        });
    }

    private static JsonObject Capabilities() => new()
    {
        ["supportsConfigurationDoneRequest"] = true,
        ["supportsSetVariable"] = true,
        ["supportsConditionalBreakpoints"] = true,
        ["supportsExceptionInfoRequest"] = true,
        ["supportsTerminateRequest"] = true,
        // Lets the editor say on a disconnect whether the debuggee should die with the session,
        // rather than leaving the adapter to guess it from how the session started.
        ["supportTerminateDebuggee"] = true,
        ["supportsEvaluateForHovers"] = true,
        // These three are emulated by PublishingDebugBackend rather than by the engine.
        ["supportsHitConditionalBreakpoints"] = true,
        ["supportsLogPoints"] = true,
        ["supportsDataBreakpoints"] = true,
        // Set Next Statement and Run to Cursor, which the ICorDebug engine has always had.
        ["supportsGotoTargetsRequest"] = true,
        ["supportsStepBack"] = false,
        ["exceptionBreakpointFilters"] = new JsonArray
        {
            new JsonObject { ["filter"] = "all", ["label"] = "All Exceptions" },
        },
    };

    private async Task ReportStopAsync(string defaultReason)
    {
        var frame = _backend.CurrentFrame;
        if (frame is null)
        {
            // No frame after a resume means the wait gave up, not that the process died — which
            // is the ordinary outcome of continuing a web request that hits nothing else. Ending
            // the session here killed it a minute after every such Continue. An engine that
            // reports its own lifecycle announces a real exit as a notice instead.
            if (_engine is null)
            {
                await EventAsync("terminated", null);
                _running = false;
            }
            return;
        }

        if (!ClaimStop())
            return;

        // A value change outranks the reason the resume was started with: the user pressed
        // Continue, but what actually stopped them is the watch.
        var dataHit = (_backend as PublishingDebugBackend)?.DataBreakpoints.LastHit;

        await EventAsync("stopped", new JsonObject
        {
            ["reason"] = dataHit is not null ? "data breakpoint"
                : frame.ExceptionName is { Length: > 0 } ? "exception"
                : defaultReason,
            ["threadId"] = 1,
            ["allThreadsStopped"] = true,
            ["description"] = dataHit?.Description,
            ["text"] = dataHit?.Description ?? frame.ExceptionMessage,
        });
    }

    /// <summary>
    /// Takes ownership of announcing the current stop, or reports that someone already has.
    /// </summary>
    /// <remarks>
    /// Both routes to a stop are legitimate — a command that resumed into it, and the engine
    /// reporting it unprompted — and neither can be dropped: the first is the only one that fires
    /// for a step, the second the only one that fires for a breakpoint hit in a running app.
    /// Which of them gets there first is a scheduling race, so they are deduplicated by the
    /// engine's stop number rather than by trying to order them. Left undeduplicated, a stop
    /// reported twice put the editor back into the state it had just left, which is why a line
    /// stayed highlighted after Continue.
    /// </remarks>
    private bool ClaimStop()
    {
        long sequence = _engine?.StopSequence ?? 0;
        if (sequence == 0)
            return true; // an engine that does not number its stops cannot report them twice

        return Interlocked.Exchange(ref _reportedStop, sequence) != sequence;
    }

    // --- Wire format ---

    private async Task<JsonNode?> ReadMessageAsync(CancellationToken ct)
    {
        int length = 0;
        var header = new StringBuilder();

        while (true)
        {
            int read = _input.ReadByte();
            if (read < 0)
                return null;

            char ch = (char)read;
            if (ch != '\n')
            {
                if (ch != '\r')
                    header.Append(ch);
                continue;
            }

            string line = header.ToString();
            header.Clear();

            if (line.Length == 0)
                break; // blank line ends the header block

            const string prefix = "Content-Length:";
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                int.TryParse(line[prefix.Length..].Trim(), out length);
        }

        if (length <= 0)
            return null;

        var buffer = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            int read = await _input.ReadAsync(buffer.AsMemory(offset, length - offset), ct);
            if (read == 0)
                return null;
            offset += read;
        }

        try
        {
            return JsonNode.Parse(Encoding.UTF8.GetString(buffer));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private Task RespondAsync(JsonNode request, JsonNode? body, bool success = true, string? message = null)
    {
        var response = new JsonObject
        {
            ["type"] = "response",
            ["request_seq"] = request["seq"]?.DeepClone(),
            ["command"] = request["command"]?.GetValue<string>(),
            ["success"] = success,
        };
        if (body is not null)
            response["body"] = body;
        if (message is not null)
            response["message"] = message;

        return SendAsync(response);
    }

    /// <summary>
    /// The DAP <c>process</c> event: which process this session is now debugging.
    /// </summary>
    /// <remarks>
    /// Part of the protocol, and load-bearing here — the editor registers the app it launched
    /// from this event, and without it a .NET Framework launch is invisible to everything that
    /// reads the running-process registry.
    /// </remarks>
    private async Task ProcessEventAsync(JsonNode? arguments, string startMethod)
    {
        // The launch path has to ask the backend: only it knows the PID of a process the engine
        // started. Attach was handed one, and reports it even if the backend is slow to record it.
        int pid = _backend.DebuggeePid
            ?? arguments?["processId"]?.GetValue<int>()
            ?? 0;
        if (pid <= 0)
            return;

        string? name = arguments?["program"]?.GetValue<string>()
            ?? arguments?["projectPath"]?.GetValue<string>();

        await EventAsync("process", new JsonObject
        {
            ["name"] = name ?? $"pid {pid}",
            ["systemProcessId"] = pid,
            ["isLocalProcess"] = true,
            ["startMethod"] = startMethod,
        });
    }

    private Task EventAsync(string name, JsonNode? body)
    {
        var message = new JsonObject
        {
            ["type"] = "event",
            ["event"] = name,
        };
        if (body is not null)
            message["body"] = body;

        return SendAsync(message);
    }

    private async Task SendAsync(JsonObject message)
    {
        message["seq"] = Interlocked.Increment(ref _sequence);
        byte[] payload = Encoding.UTF8.GetBytes(message.ToJsonString());
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");

        // One writer at a time: interleaved frames are unparseable, and stops are emitted from
        // request handlers running concurrently.
        await _writeLock.WaitAsync();
        try
        {
            await _output.WriteAsync(header);
            await _output.WriteAsync(payload);
            await _output.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }
}

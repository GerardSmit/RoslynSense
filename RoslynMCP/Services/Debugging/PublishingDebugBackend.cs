using System.Collections.Concurrent;
using System.Text;

namespace RoslynMCP.Services.Debugging;

/// <summary>
/// Decorates an <see cref="IDebugBackend"/> so every state transition is mirrored to
/// <see cref="DebugStateStore"/> — that file is what the editor polls to show the LLM's
/// debug session (paused line, reason) without holding a second debugger on the process.
/// </summary>
internal sealed class PublishingDebugBackend : IDebugBackend, IDebugNoticeSource
{
    /// <summary>How many swallowed stops to resume through before giving up and surfacing one.</summary>
    private const int MaxEmulatedResumes = 10_000;

    private const int MaxBufferedLogLines = 500;

    /// <summary>A breakpoint's emulated behavior — neither engine implements either field.</summary>
    private sealed record EmulatedBreakpoint(string? HitCondition, string? LogMessage);

    private readonly IDebugBackend _inner;
    private readonly DataBreakpointWatcher _watcher;
    /// <summary>Mirrored breakpoints. DAP handlers run concurrently, so every mutation and the
    /// snapshot <see cref="Publish"/> takes have to agree on one at a time.</summary>
    private readonly List<DebugStateStore.Breakpoint> _breakpoints = [];
    private readonly Lock _breakpointGate = new();
    private readonly ConcurrentDictionary<int, EmulatedBreakpoint> _emulated = new();
    private readonly ConcurrentDictionary<int, int> _hits = new();
    private readonly ConcurrentQueue<string> _log = new();
    private string _kind = "attach";
    private string _target = "";
    private bool _started;

    /// <summary>Set while a resuming command is in flight. The chat and the editor's mirror
    /// adapter drive this same backend, and the engine has a single stop signal — two racing
    /// resumes can steal each other's release, so the second fails fast instead.</summary>
    private int _resuming;

    /// <summary>Listeners for the decorator's own notices (<see cref="DebugNoticeKind.Resumed"/>);
    /// the engine's notices reach listeners directly via the pass-through subscription.</summary>
    private Action<DebugNotice>? _ownNotice;

    public PublishingDebugBackend(IDebugBackend inner)
    {
        _inner = inner;
        _watcher = new DataBreakpointWatcher(inner);
    }

    /// <summary>The wrapped engine — for engine-selection assertions and diagnostics.</summary>
    public IDebugBackend Inner => _inner;

    /// <summary>The engine's notices pass straight through, unchanged; the decorator adds only
    /// <see cref="DebugNoticeKind.Resumed"/>, which no engine reports. An engine that reports
    /// nothing leaves this silent rather than absent, so a caller does not have to know which
    /// engine it got.</summary>
    public event Action<DebugNotice>? Notice
    {
        add
        {
            _ownNotice += value;
            if (_inner is IDebugNoticeSource source) source.Notice += value;
        }
        remove
        {
            _ownNotice -= value;
            if (_inner is IDebugNoticeSource source) source.Notice -= value;
        }
    }

    /// <inheritdoc />
    public long StopSequence => _inner is IDebugNoticeSource source ? source.StopSequence : 0;

    /// <summary>The armed value watches. Empty unless the client set one, because watching costs a
    /// step per statement.</summary>
    public DataBreakpointWatcher DataBreakpoints => _watcher;

    public DebuggerService.StoppedFrame? CurrentFrame => _inner.CurrentFrame;

    /// <inheritdoc />
    public int? DebuggeePid => _inner.DebuggeePid;

    public async Task<string> StartTestSessionAsync(
        string csprojPath, string? filter,
        IEnumerable<(string file, int line)>? initialBreakpoints = null,
        CancellationToken cancellationToken = default)
    {
        _kind = "test";
        _target = csprojPath;
        TrackInitialBreakpoints(initialBreakpoints);
        var result = await _inner.StartTestSessionAsync(csprojPath, filter, initialBreakpoints, cancellationToken);
        _started = true;
        Publish();
        return result;
    }

    public async Task<string> AttachToProcessAsync(
        int pid, IEnumerable<(string file, int line)>? initialBreakpoints = null,
        CancellationToken cancellationToken = default)
    {
        _kind = "attach";
        _target = pid.ToString();
        TrackInitialBreakpoints(initialBreakpoints);
        var result = await _inner.AttachToProcessAsync(pid, initialBreakpoints, cancellationToken);
        _started = true;
        Publish();
        return result;
    }

    /// <summary>
    /// Launches through the ICorDebug backend, which is the only one that can start a target
    /// rather than attach to it, and publishes the session like any other.
    /// </summary>
    public async Task<string> LaunchAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        string? workingDirectory,
        IEnumerable<(string file, int line)>? initialBreakpoints = null,
        CancellationToken cancellationToken = default)
    {
        if (_inner is not IcorDebugBackend engine)
            return "Error: this engine cannot launch a target; attach to a running process instead.";

        _kind = "launch";
        _target = executable;
        TrackInitialBreakpoints(initialBreakpoints);

        var result = await engine.LaunchAsync(
            executable, arguments, environment, workingDirectory, initialBreakpoints, cancellationToken);

        // Only a started session belongs in the store — a failed launch has nothing to mirror.
        _started = !result.StartsWith("Error", StringComparison.OrdinalIgnoreCase);
        if (_started)
            Publish();

        return result;
    }

    public async Task<(string Message, int? BreakpointId)> SetBreakpointAsync(
        string filePath, int line, string? condition = null, string? hitCondition = null,
        string? logMessage = null, CancellationToken cancellationToken = default)
    {
        var result = await _inner.SetBreakpointAsync(
            filePath, line, condition, hitCondition, logMessage, cancellationToken);

        if (result.BreakpointId is { } id)
        {
            lock (_breakpointGate)
            {
                _breakpoints.RemoveAll(b => b.Id == id);
                _breakpoints.Add(new DebugStateStore.Breakpoint(id, Path.GetFullPath(filePath), line, condition));
            }

            _hits.TryRemove(id, out _);
            if (hitCondition is { Length: > 0 } || logMessage is { Length: > 0 })
                _emulated[id] = new EmulatedBreakpoint(hitCondition, logMessage);
            else
                _emulated.TryRemove(id, out _);
        }
        Publish();
        return result;
    }

    public async Task<string> RemoveBreakpointAsync(int breakpointId, CancellationToken cancellationToken = default)
    {
        var result = await _inner.RemoveBreakpointAsync(breakpointId, cancellationToken);
        lock (_breakpointGate)
            _breakpoints.RemoveAll(b => b.Id == breakpointId);
        _emulated.TryRemove(breakpointId, out _);
        _hits.TryRemove(breakpointId, out _);
        Publish();
        return result;
    }

    /// <summary>
    /// Replaces the armed data breakpoints, which is what DAP's <c>setDataBreakpoints</c> means:
    /// the client sends the whole set every time, so anything absent is removed.
    /// </summary>
    public Task<IReadOnlyList<DataBreakpointStatus>> SetDataBreakpointsAsync(
        IReadOnlyList<DataBreakpointSpec> specs, CancellationToken cancellationToken = default) =>
        _watcher.SetAsync(specs, cancellationToken);

    public Task<string> ContinueAsync(CancellationToken cancellationToken = default) =>
        _watcher.Any
            ? WatchedContinueAsync(cancellationToken)
            : ResumeAsync(() => _inner.ContinueAsync(cancellationToken), cancellationToken);

    /// <summary>
    /// Continue, when a data breakpoint is armed, is a step-and-compare walk rather than a resume:
    /// a plain continue would run straight past the write we are waiting for.
    /// </summary>
    private Task<string> WatchedContinueAsync(CancellationToken cancellationToken) =>
        GuardedResumeAsync(async () =>
        {
            var (outcome, message) = await _watcher.ContinueAsync(
                async () => !await ShouldResumeThroughAsync(cancellationToken),
                cancellationToken);

            return outcome switch
            {
                DataWatchOutcome.Changed => $"Data breakpoint hit — {message}",
                _ => message,
            };
        });

    public Task<string> StepInAsync(CancellationToken cancellationToken = default) =>
        ResumeAsync(() => _inner.StepInAsync(cancellationToken), cancellationToken);

    public Task<string> StepOverAsync(CancellationToken cancellationToken = default) =>
        ResumeAsync(() => _inner.StepOverAsync(cancellationToken), cancellationToken);

    public Task<string> StepOutAsync(CancellationToken cancellationToken = default) =>
        ResumeAsync(() => _inner.StepOutAsync(cancellationToken), cancellationToken);

    public Task<string> EvaluateAsync(string expression, CancellationToken cancellationToken = default) =>
        _inner.EvaluateAsync(expression, cancellationToken);

    public Task<string> GetLocalsAsync(CancellationToken cancellationToken = default) =>
        _inner.GetLocalsAsync(cancellationToken);

    public Task<string> GetStackTraceAsync(CancellationToken cancellationToken = default) =>
        _inner.GetStackTraceAsync(cancellationToken);

    public Task<string> InterruptAsync(CancellationToken cancellationToken = default) =>
        PublishAfter(_inner.InterruptAsync(cancellationToken));

    public Task<IReadOnlyList<StackFrameInfo>> GetStackFramesAsync(CancellationToken cancellationToken = default) =>
        _inner.GetStackFramesAsync(cancellationToken);

    public Task<IReadOnlyList<VariableInfo>> GetVariablesAsync(
        int frameId, CancellationToken cancellationToken = default) =>
        _inner.GetVariablesAsync(frameId, cancellationToken);

    public Task<IReadOnlyList<VariableInfo>> GetVariableChildrenAsync(
        int variablesReference, CancellationToken cancellationToken = default) =>
        _inner.GetVariableChildrenAsync(variablesReference, cancellationToken);

    public Task<(bool Ok, string Value, string Error)> SetVariableAsync(
        string name, string value, int frameId = 0, CancellationToken cancellationToken = default) =>
        _inner.SetVariableAsync(name, value, frameId, cancellationToken);

    public Task<string> SelectFrameAsync(int frameId, CancellationToken cancellationToken = default) =>
        _inner.SelectFrameAsync(frameId, cancellationToken);

    public Task<IReadOnlyList<ThreadInfo>> GetThreadsAsync(CancellationToken cancellationToken = default) =>
        _inner.GetThreadsAsync(cancellationToken);

    public Task<ExceptionDetail?> GetExceptionInfoAsync(CancellationToken cancellationToken = default) =>
        _inner.GetExceptionInfoAsync(cancellationToken);

    public Task<string> SetExceptionFiltersAsync(
        ExceptionFilters filters, CancellationToken cancellationToken = default) =>
        _inner.SetExceptionFiltersAsync(filters, cancellationToken);

    public Task<string> RunToLocationAsync(
        string filePath, int line, CancellationToken cancellationToken = default) =>
        GuardedResumeAsync(() => _inner.RunToLocationAsync(filePath, line, cancellationToken));

    public Task<string> SetNextStatementAsync(
        string filePath, int line, CancellationToken cancellationToken = default) =>
        PublishAfter(_inner.SetNextStatementAsync(filePath, line, cancellationToken));

    public Task<IReadOnlyList<ModuleInfo>> GetModulesAsync(CancellationToken cancellationToken = default) =>
        _inner.GetModulesAsync(cancellationToken);

    public Task<string> DetachAsync(CancellationToken cancellationToken = default) =>
        PublishAfter(_inner.DetachAsync(cancellationToken));

    public string GetStatus() => _inner.GetStatus();

    public string Stop()
    {
        var result = _inner.Stop();
        _watcher.Clear();
        DebugStateStore.Clear(Environment.ProcessId);
        _started = false;
        return result;
    }

    public void Dispose()
    {
        DebugStateStore.Clear(Environment.ProcessId);
        _inner.Dispose();
    }

    /// <summary>
    /// Resumes, then keeps resuming through stops the user should never see: a breakpoint whose
    /// hit condition is not met yet, and a logpoint, which logs and continues.
    /// </summary>
    /// <remarks>
    /// This is where hit counts and logpoints exist at all — netcoredbg advertises neither, and
    /// ICorDebug's hit skipping is set-once at bind time. Doing it here means both the MCP tools
    /// and the editor's mirror adapter get the same behavior for free.
    /// </remarks>
    private Task<string> ResumeAsync(Func<Task<string>> operation, CancellationToken cancellationToken) =>
        GuardedResumeAsync(async () =>
        {
            string result = await operation();

            // A logpoint inside a hot loop would otherwise resume forever; surface the stop
            // rather than hang the caller.
            for (int resumes = 0; resumes < MaxEmulatedResumes; resumes++)
            {
                if (!await ShouldResumeThroughAsync(cancellationToken))
                    break;

                result = await _inner.ContinueAsync(cancellationToken);
            }

            // A step can be what writes the watched value, so the baselines are refreshed here
            // too — otherwise the next continue would report a change the user already saw.
            if (_watcher.Any && _inner.CurrentFrame is not null &&
                await _watcher.CheckAsync(cancellationToken) is { } hit)
            {
                return $"Data breakpoint hit — {hit.Description}\n{result}";
            }

            return result;
        });

    /// <summary>
    /// Wraps every resuming command: refuses a second concurrent resume, announces the
    /// transition — the state file for the polling mirror, a <see cref="DebugNoticeKind.Resumed"/>
    /// notice for a DAP host — and publishes the landing state when the command ends. This is
    /// what lets an attached editor follow a resume some other client issued.
    /// </summary>
    private async Task<string> GuardedResumeAsync(Func<Task<string>> operation)
    {
        if (Interlocked.CompareExchange(ref _resuming, 1, 0) != 0)
            return "Error: another client of this debug session is already resuming it; " +
                   "wait for the target to stop.";

        try
        {
            PublishRunning();
            _ownNotice?.Invoke(new DebugNotice(DebugNoticeKind.Resumed, ""));
            return await operation();
        }
        finally
        {
            Volatile.Write(ref _resuming, 0);
            Publish();
        }
    }

    /// <summary>
    /// Applies the emulation to a stop that no resume command asked for, resuming through it and
    /// returning <c>true</c> when the user was never meant to see it.
    /// </summary>
    /// <remarks>
    /// Hit counts and logpoints used to exist only inside <see cref="ResumeAsync"/>, so they
    /// worked only for a stop that ended a command the adapter had issued. Every other stop — the
    /// first hit after an attach, and every hit once a continue has timed out waiting on a server
    /// — went round them: the logpoint suspended the app instead of logging, and its hit was
    /// never counted.
    /// </remarks>
    public async Task<bool> ResumeThroughUnsolicitedStopAsync(CancellationToken cancellationToken = default)
    {
        if (!await ShouldResumeThroughAsync(cancellationToken))
            return false;

        try
        {
            await _inner.ContinueAsync(cancellationToken);
            return true;
        }
        finally
        {
            Publish();
        }
    }

    /// <summary>Decides whether the current stop is one the emulation swallows, doing its
    /// logging and hit counting on the way.</summary>
    private async Task<bool> ShouldResumeThroughAsync(CancellationToken cancellationToken)
    {
        var frame = _inner.CurrentFrame;
        if (frame is null || frame.BreakpointNumber <= 0)
            return false;
        if (!_emulated.TryGetValue(frame.BreakpointNumber, out var rule))
            return false;

        int hits = _hits.AddOrUpdate(frame.BreakpointNumber, 1, (_, count) => count + 1);

        if (rule.HitCondition is { Length: > 0 } hitCondition && !HitConditionMet(hitCondition, hits))
            return true;

        if (rule.LogMessage is { Length: > 0 } message)
        {
            Log(await InterpolateAsync(message, cancellationToken));
            return true;
        }

        return false;
    }

    /// <summary>
    /// Evaluates <c>{expression}</c> placeholders in a logpoint message.
    /// </summary>
    private async Task<string> InterpolateAsync(string message, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder(message.Length);

        for (int i = 0; i < message.Length; i++)
        {
            if (message[i] != '{')
            {
                sb.Append(message[i]);
                continue;
            }

            int end = message.IndexOf('}', i + 1);
            if (end < 0)
            {
                sb.Append(message[i..]);
                break;
            }

            string expression = message[(i + 1)..end];
            try
            {
                sb.Append((await _inner.EvaluateAsync(expression, cancellationToken)).Trim());
            }
            catch (Exception ex)
            {
                sb.Append($"<{ex.Message}>");
            }
            i = end;
        }

        return sb.ToString();
    }

    private void Log(string line)
    {
        _log.Enqueue(line);
        while (_log.Count > MaxBufferedLogLines)
            _log.TryDequeue(out _);
    }

    /// <summary>Takes the buffered logpoint output, leaving the buffer empty — the editor polls
    /// this to raise DAP <c>output</c> events.</summary>
    public IReadOnlyList<string> DrainLog()
    {
        var lines = new List<string>();
        while (_log.TryDequeue(out string? line))
            lines.Add(line);
        return lines;
    }

    /// <summary>
    /// Applies VS Code's hit-count vocabulary: <c>&gt; n</c>, <c>&gt;= n</c>, <c>&lt; n</c>,
    /// <c>&lt;= n</c>, <c>= n</c>, <c>% n</c>, and a bare count meaning "on hit n and after".
    /// </summary>
    internal static bool HitConditionMet(string condition, int hits)
    {
        string text = condition.Trim();

        int split = 0;
        while (split < text.Length && !char.IsAsciiDigit(text[split]))
            split++;

        string @operator = text[..split].Trim() switch
        {
            ">=" => ">=",
            "<=" => "<=",
            "==" or "=" => "=",
            ">" => ">",
            "<" => "<",
            "%" => "%",
            _ => ">=",
        };

        if (!int.TryParse(text[split..].Trim(), out int target) || target <= 0)
            return true; // an unparseable rule must not silently swallow every stop

        return @operator switch
        {
            ">" => hits > target,
            ">=" => hits >= target,
            "<" => hits < target,
            "<=" => hits <= target,
            "=" => hits == target,
            "%" => hits % target == 0,
            _ => true,
        };
    }

    private async Task<string> PublishAfter(Task<string> operation)
    {
        try
        {
            return await operation;
        }
        finally
        {
            Publish();
        }
    }

    private void Publish()
    {
        if (!_started)
            return;

        var frame = _inner.CurrentFrame;
        string state = frame is null
            ? "running"
            : frame.Reason.Contains("exited", StringComparison.OrdinalIgnoreCase) ? "exited" : "stopped";
        DebugStateStore.Publish(new DebugStateStore.Entry(
            Environment.ProcessId,
            DebugStateStore.PipeNameFor(Environment.ProcessId),
            _kind, _target, state,
            frame?.Reason, frame?.Function, frame?.FilePath, frame?.Line ?? 0,
            DateTime.UtcNow,
            Snapshot(),
            StopSequence));
    }

    /// <summary>
    /// Publishes the in-between state a resume enters. <see cref="Publish"/> reads the engine's
    /// frame, which still holds the previous stop until the resume lands — without this the
    /// mirror sees stopped→stopped and never learns the session moved.
    /// </summary>
    private void PublishRunning()
    {
        if (!_started)
            return;

        DebugStateStore.Publish(new DebugStateStore.Entry(
            Environment.ProcessId,
            DebugStateStore.PipeNameFor(Environment.ProcessId),
            _kind, _target, "running",
            null, null, null, 0,
            DateTime.UtcNow,
            Snapshot(),
            StopSequence));
    }

    /// <summary>The mirrored breakpoints, copied under the gate.</summary>
    /// <remarks>
    /// <see cref="List{T}.ToArray"/> reads Count and then the backing store; a concurrent Add can
    /// land between the two and yield a torn array or throw. Publish runs from every state
    /// transition while DAP handlers mutate the list, so this is the reader the gate exists for.
    /// </remarks>
    private DebugStateStore.Breakpoint[] Snapshot()
    {
        lock (_breakpointGate)
            return [.. _breakpoints];
    }

    /// <summary>Initial breakpoints are set inside the backend during start, which never
    /// reports their ids — tracked with id 0 (they cannot be removed individually anyway).</summary>
    private void TrackInitialBreakpoints(IEnumerable<(string file, int line)>? initialBreakpoints)
    {
        foreach (var (file, line) in initialBreakpoints ?? [])
        {
            lock (_breakpointGate)
                _breakpoints.Add(new DebugStateStore.Breakpoint(0, Path.GetFullPath(file), line, null));
        }
    }
}

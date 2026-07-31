using System.Collections.Concurrent;
using System.Text;

namespace RoslynMCP.Services.Debugging;

/// <summary>
/// Decorates an <see cref="IDebugBackend"/> so every state transition is mirrored to
/// <see cref="DebugStateStore"/> — that file is what the editor polls to show the LLM's
/// debug session (paused line, reason) without holding a second debugger on the process.
/// </summary>
internal sealed class PublishingDebugBackend : IDebugBackend
{
    /// <summary>How many swallowed stops to resume through before giving up and surfacing one.</summary>
    private const int MaxEmulatedResumes = 10_000;

    private const int MaxBufferedLogLines = 500;

    /// <summary>A breakpoint's emulated behavior — neither engine implements either field.</summary>
    private sealed record EmulatedBreakpoint(string? HitCondition, string? LogMessage);

    private readonly IDebugBackend _inner;
    private readonly List<DebugStateStore.Breakpoint> _breakpoints = [];
    private readonly ConcurrentDictionary<int, EmulatedBreakpoint> _emulated = new();
    private readonly ConcurrentDictionary<int, int> _hits = new();
    private readonly ConcurrentQueue<string> _log = new();
    private string _kind = "attach";
    private string _target = "";
    private bool _started;

    public PublishingDebugBackend(IDebugBackend inner) => _inner = inner;

    /// <summary>The wrapped engine — for engine-selection assertions and diagnostics.</summary>
    public IDebugBackend Inner => _inner;

    public DebuggerService.StoppedFrame? CurrentFrame => _inner.CurrentFrame;

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

    public async Task<(string Message, int? BreakpointId)> SetBreakpointAsync(
        string filePath, int line, string? condition = null, string? hitCondition = null,
        string? logMessage = null, CancellationToken cancellationToken = default)
    {
        var result = await _inner.SetBreakpointAsync(
            filePath, line, condition, hitCondition, logMessage, cancellationToken);

        if (result.BreakpointId is { } id)
        {
            _breakpoints.RemoveAll(b => b.Id == id);
            _breakpoints.Add(new DebugStateStore.Breakpoint(id, Path.GetFullPath(filePath), line, condition));

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
        _breakpoints.RemoveAll(b => b.Id == breakpointId);
        _emulated.TryRemove(breakpointId, out _);
        _hits.TryRemove(breakpointId, out _);
        Publish();
        return result;
    }

    public Task<string> ContinueAsync(CancellationToken cancellationToken = default) =>
        ResumeAsync(() => _inner.ContinueAsync(cancellationToken), cancellationToken);

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

    public string GetStatus() => _inner.GetStatus();

    public string Stop()
    {
        var result = _inner.Stop();
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
    private async Task<string> ResumeAsync(Func<Task<string>> operation, CancellationToken cancellationToken)
    {
        try
        {
            string result = await operation();

            // A logpoint inside a hot loop would otherwise resume forever; surface the stop
            // rather than hang the caller.
            for (int resumes = 0; resumes < MaxEmulatedResumes; resumes++)
            {
                if (!await ShouldResumeThroughAsync(cancellationToken))
                    return result;

                result = await _inner.ContinueAsync(cancellationToken);
            }

            return result;
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
            _breakpoints.ToArray()));
    }

    /// <summary>Initial breakpoints are set inside the backend during start, which never
    /// reports their ids — tracked with id 0 (they cannot be removed individually anyway).</summary>
    private void TrackInitialBreakpoints(IEnumerable<(string file, int line)>? initialBreakpoints)
    {
        foreach (var (file, line) in initialBreakpoints ?? [])
            _breakpoints.Add(new DebugStateStore.Breakpoint(0, Path.GetFullPath(file), line, null));
    }
}

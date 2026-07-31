namespace RoslynMCP.Services.Debugging;

/// <summary>
/// Decorates an <see cref="IDebugBackend"/> so every state transition is mirrored to
/// <see cref="DebugStateStore"/> — that file is what the editor polls to show the LLM's
/// debug session (paused line, reason) without holding a second debugger on the process.
/// </summary>
internal sealed class PublishingDebugBackend : IDebugBackend
{
    private readonly IDebugBackend _inner;
    private readonly List<DebugStateStore.Breakpoint> _breakpoints = [];
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
        string filePath, int line, string? condition = null, CancellationToken cancellationToken = default)
    {
        var result = await _inner.SetBreakpointAsync(filePath, line, condition, cancellationToken);
        if (result.BreakpointId is { } id)
        {
            _breakpoints.RemoveAll(b => b.Id == id);
            _breakpoints.Add(new DebugStateStore.Breakpoint(id, Path.GetFullPath(filePath), line, condition));
        }
        Publish();
        return result;
    }

    public async Task<string> RemoveBreakpointAsync(int breakpointId, CancellationToken cancellationToken = default)
    {
        var result = await _inner.RemoveBreakpointAsync(breakpointId, cancellationToken);
        _breakpoints.RemoveAll(b => b.Id == breakpointId);
        Publish();
        return result;
    }

    public Task<string> ContinueAsync(CancellationToken cancellationToken = default) =>
        PublishAfter(_inner.ContinueAsync(cancellationToken));

    public Task<string> StepInAsync(CancellationToken cancellationToken = default) =>
        PublishAfter(_inner.StepInAsync(cancellationToken));

    public Task<string> StepOverAsync(CancellationToken cancellationToken = default) =>
        PublishAfter(_inner.StepOverAsync(cancellationToken));

    public Task<string> StepOutAsync(CancellationToken cancellationToken = default) =>
        PublishAfter(_inner.StepOutAsync(cancellationToken));

    public Task<string> EvaluateAsync(string expression, CancellationToken cancellationToken = default) =>
        _inner.EvaluateAsync(expression, cancellationToken);

    public Task<string> GetLocalsAsync(CancellationToken cancellationToken = default) =>
        _inner.GetLocalsAsync(cancellationToken);

    public Task<string> GetStackTraceAsync(CancellationToken cancellationToken = default) =>
        _inner.GetStackTraceAsync(cancellationToken);

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

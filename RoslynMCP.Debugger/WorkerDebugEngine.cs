using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace RoslynMCP.Debugger;

/// <summary>
/// Drives a <see cref="DebugSession"/> hosted in a separate process whose bitness matches the
/// debug target.
/// </summary>
/// <remarks>
/// ICorDebug refuses to attach across x86/x64, so a 32-bit target cannot be debugged from this
/// 64-bit host (or vice versa). The worker runs the identical engine; this class only forwards
/// commands and republishes the worker's event stream, so callers cannot tell the difference.
/// </remarks>
public sealed class WorkerDebugEngine : IDebugEngine
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    private readonly Process _worker;
    private readonly Channel<DebugEvent> _events = Channel.CreateUnbounded<DebugEvent>();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<WorkerResponse>> _pending = new();
    private readonly Lock _writeGate = new();
    private int _nextId;
    private int _disposed;

    public ChannelReader<DebugEvent> Events => _events.Reader;

    public WorkerDebugEngine(string workerPath, uint sessionId)
    {
        _worker = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = workerPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(workerPath) ?? Environment.CurrentDirectory,
            },
            EnableRaisingEvents = true,
        };

        _worker.StartInfo.ArgumentList.Add(sessionId.ToString());

        // The worker is a different architecture from this process, so anything pinning this
        // process's runtime would send its apphost looking for a runtime of the wrong bitness and
        // it would exit immediately. Clear those and let the worker resolve its own.
        foreach (var variable in new[]
                 {
                     "DOTNET_ROOT", "DOTNET_ROOT(x86)", "DOTNET_ROOT_X86", "DOTNET_ROOT_X64",
                     "DOTNET_HOST_PATH", "DOTNET_MULTILEVEL_LOOKUP",
                     "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildSDKsPath",
                 })
        {
            _worker.StartInfo.Environment.Remove(variable);
        }

        // The worker's stderr is its log; surfacing it as output keeps a crash diagnosable.
        _worker.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { Length: > 0 })
                _events.Writer.TryWrite(new DebugEvent { Kind = DebugEventKind.Output, Message = $"[worker] {e.Data}" });
        };

        _worker.Exited += (_, _) =>
        {
            FailPending("the debug worker exited");
            _events.Writer.TryComplete();
        };

        _worker.Start();
        _worker.BeginErrorReadLine();

        _ = Task.Run(ReadLoopAsync);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await _worker.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.Length == 0)
                    continue;

                WorkerResponse? message;
                try
                {
                    message = JsonSerializer.Deserialize<WorkerResponse>(line, WorkerProtocol.Options);
                }
                catch (JsonException)
                {
                    continue; // Not protocol traffic; ignore rather than tear the session down.
                }

                if (message is null)
                    continue;

                if (message.Event is { } debugEvent)
                {
                    _events.Writer.TryWrite(debugEvent);
                    if (debugEvent.Kind == DebugEventKind.Exited)
                        _events.Writer.TryComplete();
                    continue;
                }

                if (_pending.TryRemove(message.Id, out var waiter))
                    waiter.TrySetResult(message);
            }
        }
        catch (Exception ex)
        {
            FailPending(ex.Message);
        }
        finally
        {
            FailPending("the debug worker closed its output");
            _events.Writer.TryComplete();
        }
    }

    private void FailPending(string reason)
    {
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var waiter))
                waiter.TrySetException(new InvalidOperationException(reason));
        }
    }

    private async Task<WorkerResponse> SendAsync(WorkerRequest request)
    {
        if (_disposed != 0 || _worker.HasExited)
            throw new InvalidOperationException("the debug worker is not running");

        request.Id = Interlocked.Increment(ref _nextId);

        var waiter = new TaskCompletionSource<WorkerResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.Id] = waiter;

        var line = JsonSerializer.Serialize(request, WorkerProtocol.Options);
        lock (_writeGate)
        {
            _worker.StandardInput.WriteLine(line);
            _worker.StandardInput.Flush();
        }

        var response = await waiter.Task.WaitAsync(RequestTimeout);
        return response.Ok
            ? response
            : throw new InvalidOperationException(
                response.Error.Length > 0 ? response.Error : $"the worker rejected '{request.Op}'");
    }

    /// <summary>Fire-and-forget for commands whose failure surfaces as a later event.</summary>
    private void Send(WorkerRequest request)
    {
        try
        {
            SendAsync(request).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _events.Writer.TryWrite(new DebugEvent
            {
                Kind = DebugEventKind.Output,
                Message = $"[worker] {request.Op} failed: {ex.Message}",
            });
        }
    }

    public void Attach(int pid, IEnumerable<BreakpointSpec> breakpoints, DebugRuntime runtime) =>
        SendAsync(new WorkerRequest
        {
            Op = "attach",
            Pid = pid,
            Runtime = runtime,
            Breakpoints = [.. breakpoints],
        }).GetAwaiter().GetResult();

    public void Launch(
        string executable, IReadOnlyList<string> arguments, IEnumerable<BreakpointSpec> breakpoints,
        IReadOnlyDictionary<string, string>? environment, string? workingDirectory,
        DebugRuntime runtime) =>
        SendAsync(new WorkerRequest
        {
            Op = "launch",
            Executable = executable,
            Arguments = [.. arguments],
            Environment = environment is null ? null : new Dictionary<string, string>(environment),
            WorkingDirectory = workingDirectory,
            Runtime = runtime,
            Breakpoints = [.. breakpoints],
        }).GetAwaiter().GetResult();

    public void AddBreakpoint(BreakpointSpec spec) =>
        Send(new WorkerRequest { Op = "addBreakpoint", Breakpoint = spec });

    public bool RemoveBreakpoint(string filePath, int line)
    {
        try
        {
            return SendAsync(new WorkerRequest { Op = "removeBreakpoint", FilePath = filePath, Line = line })
                .GetAwaiter().GetResult().Removed;
        }
        catch
        {
            return false;
        }
    }

    public void Continue() => Send(new WorkerRequest { Op = "continue" });

    public void Step(StepKind kind) => Send(new WorkerRequest { Op = "step", Step = kind });

    public async Task<List<StackFrame>> StackTraceAsync() =>
        (await SendAsync(new WorkerRequest { Op = "stackTrace" })).Frames ?? [];

    public async Task<List<DebugVariable>> VariablesAsync(uint frameIndex) =>
        (await SendAsync(new WorkerRequest { Op = "variables", FrameIndex = frameIndex })).Variables ?? [];

    public async Task<(bool Ok, string Value, string Error)> EvaluateAsync(uint frameIndex, string expression)
    {
        try
        {
            var response = await SendAsync(new WorkerRequest
            {
                Op = "evaluate",
                FrameIndex = frameIndex,
                Expression = expression,
            });

            return (true, response.Value ?? "", "");
        }
        catch (Exception ex)
        {
            return (false, "", ex.Message);
        }
    }

    public async Task<(bool Ok, DebugVariable? Variable, string Error)> SetVariableAsync(
        uint frameIndex, string name, string value)
    {
        try
        {
            var response = await SendAsync(new WorkerRequest
            {
                Op = "setVariable",
                FrameIndex = frameIndex,
                Name = name,
                Value = value,
            });

            return (true, response.Variable, "");
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    public void Terminate()
    {
        try { Send(new WorkerRequest { Op = "terminate" }); } catch { }
        Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            if (!_worker.WaitForExit(3000))
                _worker.Kill(entireProcessTree: true);
        }
        catch
        {
            // The worker may already be gone.
        }

        try { _worker.Dispose(); } catch { }
        _events.Writer.TryComplete();
    }
}

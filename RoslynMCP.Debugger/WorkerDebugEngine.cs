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
    private int _detached;

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
                _events.Writer.TryWrite(new DebugEvent { Kind = DebugEventKind.Diagnostic, Message = $"[worker] {e.Data}" });
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

    private Task<WorkerResponse> SendAsync(WorkerRequest request) =>
        SendAsync(request, RequestTimeout);

    private async Task<WorkerResponse> SendAsync(WorkerRequest request, TimeSpan timeout)
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

        try
        {
            var response = await waiter.Task.WaitAsync(timeout);
            return response.Ok
                ? response
                : throw new InvalidOperationException(
                    response.Error.Length > 0 ? response.Error : $"the worker rejected '{request.Op}'");
        }
        finally
        {
            // A timed-out request is never answered, so nothing else would ever drop its waiter.
            _pending.TryRemove(request.Id, out _);
        }
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
                Kind = DebugEventKind.Diagnostic,
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

    public void Pause() => Send(new WorkerRequest { Op = "pause" });

    public void Step(StepKind kind) => Send(new WorkerRequest { Op = "step", Step = kind });

    public async Task<List<StackFrame>> StackTraceAsync() =>
        (await SendAsync(new WorkerRequest { Op = "stackTrace" })).Frames ?? [];

    public async Task<List<DebugVariable>> VariablesAsync(uint frameIndex) =>
        (await SendAsync(new WorkerRequest { Op = "variables", FrameIndex = frameIndex })).Variables ?? [];

    public async Task<List<DebugVariable>> ExpandAsync(uint frameIndex, string path) =>
        (await SendAsync(new WorkerRequest { Op = "expand", FrameIndex = frameIndex, Path = path }))
        .Variables ?? [];

    /// <summary>
    /// Forwards the display policy to the worker's engine. Fire-and-forget, like the other
    /// void commands: the settings are applied before anything can be inspected, and a worker
    /// that cannot take them has already failed louder elsewhere.
    /// </summary>
    public void SetDisplayOptions(DebugDisplayOptions options) =>
        Send(new WorkerRequest { Op = "displayOptions", DisplayOptions = options });

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

    public async Task<(bool Ok, string Error)> ApplyDeltaAsync(
        string assemblyName, byte[] metadata, byte[] il, byte[] pdb)
    {
        try
        {
            var response = await SendAsync(new WorkerRequest
            {
                Op = "applyDelta",
                Name = assemblyName,
                MetadataDelta = Convert.ToBase64String(metadata),
                IlDelta = Convert.ToBase64String(il),
                PdbDelta = Convert.ToBase64String(pdb),
            });

            // A successful response still carries a message when the edit was queued rather
            // than applied (DebugSession.DeltaQueuedPrefix); the caller tells the user which.
            return (true, response.Error);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<RunToLocationResponse> RunToLocationAsync(RunToLocationRequest request)
    {
        try
        {
            var response = await SendAsync(new WorkerRequest
            {
                Op = "runToLocation",
                FilePath = request.Location?.FilePath,
                Line = (int)(request.Location?.Line ?? 0),
                Force = request.Force,
                ModulePath = request.ModulePath.Length > 0 ? request.ModulePath : null,
                MethodToken = request.MethodToken,
                IlOffset = request.IlOffset,
            });

            return response.RunToLocation ?? new RunToLocationResponse { Ok = true };
        }
        catch (Exception ex)
        {
            return new RunToLocationResponse { Ok = false, Error = ex.Message };
        }
    }

    public async Task<SetNextStatementResponse> SetNextStatementAsync(SetNextStatementRequest request)
    {
        try
        {
            var response = await SendAsync(new WorkerRequest
            {
                Op = "setNextStatement",
                FrameIndex = request.FrameIndex,
                FilePath = request.Location?.FilePath,
                Line = (int)(request.Location?.Line ?? 0),
                MethodToken = request.MethodToken,
                IlOffset = request.IlOffset,
            });

            return response.SetNextStatement ?? new SetNextStatementResponse { Ok = true };
        }
        catch (Exception ex)
        {
            return new SetNextStatementResponse { Ok = false, Error = ex.Message };
        }
    }

    public async Task<List<DebugModule>> ModulesAsync() =>
        (await SendAsync(new WorkerRequest { Op = "modules" })).Modules ?? [];

    public async Task<(bool Ok, string Error)> DetachAsync()
    {
        try
        {
            await SendAsync(new WorkerRequest { Op = "detach" });
            Interlocked.Exchange(ref _detached, 1);
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public void SetExceptionPolicy(bool breakOnFirstChance) =>
        Send(new WorkerRequest { Op = "exceptionPolicy", Flag = breakOnFirstChance });

    public async Task<(bool Graceful, string Error)> ShutdownAsync(TimeSpan timeout)
    {
        try
        {
            // The worker runs the shutdown and only answers once the debuggee is gone, so this
            // wait has to outlast the timeout it was given rather than the usual request budget.
            var response = await SendAsync(
                new WorkerRequest { Op = "shutdown", TimeoutSeconds = timeout.TotalSeconds },
                timeout + TimeSpan.FromSeconds(10));
            return (response.Graceful, response.Error);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            Dispose();
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
            // The worker's only exit signal is end-of-input: one that was never sent 'terminate'
            // blocks in ReadLineAsync until the wait below expires and it is killed.
            if (!_worker.HasExited)
                _worker.StandardInput.Close();
        }
        catch
        {
            // Already gone, or its input is already closed.
        }

        try
        {
            // Detach promised the debuggee would keep running, so after one the worker's tree is
            // off limits and only the worker itself may be taken down.
            if (!_worker.WaitForExit(3000))
                _worker.Kill(entireProcessTree: Volatile.Read(ref _detached) == 0);
        }
        catch
        {
            // The worker may already be gone.
        }

        try { _worker.Dispose(); } catch { }
        _events.Writer.TryComplete();
    }
}

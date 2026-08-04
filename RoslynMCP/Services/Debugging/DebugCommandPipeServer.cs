using System.IO.Pipes;
using RoslynMCP.Daemon;

namespace RoslynMCP.Services.Debugging;

/// <summary>One editor-issued command against the chat-owned debug session.</summary>
internal sealed record DebugPipeRequest(
    string Action,          // continue | step_in | step_over | step_out | pause | evaluate |
                            // locals | stacktrace | status | stop | set_breakpoint |
                            // remove_breakpoint | frames | variables | children | set_variable |
                            // threads | exception_info | exception_filters | drain_log |
                            // set_data_breakpoints | data_hit | apply_delta
    string? Expression = null,
    string? File = null,
    int Line = 0,
    string? Condition = null,
    int BreakpointId = 0,
    string? HitCondition = null,
    string? LogMessage = null,
    int FrameId = 0,
    int VariablesReference = 0,
    string? Value = null,
    string[]? Filters = null,
    DataBreakpointSpec[]? DataBreakpoints = null,
    string? AssemblyName = null,
    string? MetadataDelta = null,
    string? IlDelta = null,
    string? PdbDelta = null);

internal sealed record DebugPipeResponse(bool Ok, string? Result, string? Error);

/// <summary>
/// Command channel into a chat-owned debug session. The debugger lives in the MCP client
/// process (<c>[InProcessOnly]</c>), but the editor's debug controls arrive at the shared
/// daemon — this pipe is how the daemon reaches back into the owning process. One request
/// per connection, mirroring the daemon's own accept loop.
/// </summary>
internal sealed class DebugCommandPipeServer : IDisposable
{
    private readonly Func<IDebugBackend?> _sessionProvider;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();

    public DebugCommandPipeServer(Func<IDebugBackend?> sessionProvider, string? pipeName = null)
    {
        _sessionProvider = sessionProvider;
        _pipeName = pipeName ?? DebugStateStore.PipeNameFor(Environment.ProcessId);
        _ = AcceptLoopAsync(_cts.Token);
    }

    public string PipeName => _pipeName;

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(
                    _pipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            }
            catch (IOException)
            {
                return; // pipe name already taken (another session in this process) — give up
            }

            try
            {
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                break;
            }
            catch (IOException)
            {
                await pipe.DisposeAsync();
                continue;
            }

            _ = HandleConnectionAsync(pipe, ct);
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            await using (pipe)
            {
                var request = await IpcProtocol.ReadMessageAsync<DebugPipeRequest>(pipe, ct);
                if (request is null)
                    return;

                var response = await ExecuteAsync(request, ct);
                // None, not ct: a "stop" command disposes this server (cancelling ct) while
                // its own response is still in flight.
                await IpcProtocol.WriteMessageAsync(pipe, response, CancellationToken.None);
                if (OperatingSystem.IsWindows())
                {
                    try { pipe.WaitForPipeDrain(); } catch { }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or EndOfStreamException)
        {
            // Editor vanished mid-command; the session itself is unaffected.
        }
    }

    private async Task<DebugPipeResponse> ExecuteAsync(DebugPipeRequest request, CancellationToken ct)
    {
        var session = _sessionProvider();
        if (session is null)
            return new DebugPipeResponse(false, null, "No active debug session.");

        try
        {
            string result = request.Action switch
            {
                "continue" => await session.ContinueAsync(ct),
                "step_in" => await session.StepInAsync(ct),
                "step_over" => await session.StepOverAsync(ct),
                "step_out" => await session.StepOutAsync(ct),
                "evaluate" when !string.IsNullOrWhiteSpace(request.Expression) =>
                    await session.EvaluateAsync(request.Expression!, ct),
                "pause" => await session.InterruptAsync(ct),
                "locals" => await session.GetLocalsAsync(ct),
                "stacktrace" => await session.GetStackTraceAsync(ct),
                "status" => session.GetStatus(),
                "stop" => StopSession(),
                "set_breakpoint" when request.File is not null =>
                    (await session.SetBreakpointAsync(
                        request.File, request.Line, request.Condition,
                        request.HitCondition, request.LogMessage, ct)).Message,

                // Structured actions answer with JSON so the editor's views get real data rather
                // than a regex reading the markdown surface back apart.
                "frames" => Json(await session.GetStackFramesAsync(ct)),
                "variables" => Json(await session.GetVariablesAsync(request.FrameId, ct)),
                "children" => Json(await session.GetVariableChildrenAsync(request.VariablesReference, ct)),
                "threads" => Json(await session.GetThreadsAsync(ct)),
                "exception_info" => Json(await session.GetExceptionInfoAsync(ct)),
                "set_variable" when request.Expression is not null =>
                    Json(await session.SetVariableAsync(
                        request.Expression, request.Value ?? "", request.FrameId, ct)),
                "exception_filters" => await session.SetExceptionFiltersAsync(
                    ExceptionFilters.FromIds(request.Filters ?? []), ct),
                // Value watches live in the decorator, not the engine, so both are answered by
                // the wrapper or refused rather than reaching the engine at all.
                "set_data_breakpoints" => session is PublishingDebugBackend watching
                    ? Json(await watching.SetDataBreakpointsAsync(request.DataBreakpoints ?? [], ct))
                    : throw new NotSupportedException("This session cannot watch values."),
                "data_hit" => Json((session as PublishingDebugBackend)?.DataBreakpoints.LastHit),

                // Hot reload on .NET Framework has to travel this way: the delta is computed where
                // the workspace is loaded, but ICorDebug can only apply it from the process that
                // owns the debug session.
                "apply_delta" when request.AssemblyName is { Length: > 0 } => await ApplyDeltaAsync(
                    session, request, ct),
                "drain_log" => Json(
                    (session as PublishingDebugBackend)?.DrainLog() ?? (IReadOnlyList<string>)[]),
                "remove_breakpoint" when request.BreakpointId > 0 =>
                    await session.RemoveBreakpointAsync(request.BreakpointId, ct),
                _ => throw new ArgumentException($"Unknown or malformed action '{request.Action}'."),
            };
            return new DebugPipeResponse(true, result, null);
        }
        catch (Exception ex)
        {
            return new DebugPipeResponse(false, null, ex.Message);
        }
    }

    private static async Task<string> ApplyDeltaAsync(
        IDebugBackend session, DebugPipeRequest request, CancellationToken ct)
    {
        var engine = (session as PublishingDebugBackend)?.Inner ?? session;
        if (engine is not IcorDebugBackend icor)
            return "Error: this session does not debug .NET Framework, so it cannot apply a delta.";

        var (ok, error) = await icor.ApplyDeltaAsync(
            request.AssemblyName!,
            Convert.FromBase64String(request.MetadataDelta ?? ""),
            Convert.FromBase64String(request.IlDelta ?? ""),
            Convert.FromBase64String(request.PdbDelta ?? ""),
            ct);

        return ok ? "Applied." : $"Error: {error}";
    }

    private static string Json<T>(T value) =>
        System.Text.Json.JsonSerializer.Serialize(value, DebugJson.Options);

    private string StopSession()
    {
        var managed = DebugSessionManager.GetSession();
        if (managed is null)
        {
            // A DAP-hosted session owns its backend directly and was never registered with the
            // manager, so the manager path would report success while stopping nothing.
            return _sessionProvider()?.Stop() ?? "No active debug session.";
        }

        // Route through the manager so the pipe server + published state are torn down with
        // the session, exactly as the DebugStop tool does.
        var result = managed.Stop();
        DebugSessionManager.DisposeSession();
        return result;
    }

    /// <summary>Connects to the command pipe of the debug session owned by
    /// <paramref name="ownerPid"/> and executes one command.</summary>
    public static async Task<DebugPipeResponse> SendAsync(
        int ownerPid, DebugPipeRequest request, CancellationToken ct)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".", DebugStateStore.PipeNameFor(ownerPid),
                PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(TimeSpan.FromSeconds(3), ct);
            await IpcProtocol.WriteMessageAsync(pipe, request, ct);
            var response = await IpcProtocol.ReadMessageAsync<DebugPipeResponse>(pipe, ct);
            return response ?? new DebugPipeResponse(false, null, "Debug session closed the connection.");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or EndOfStreamException)
        {
            return new DebugPipeResponse(false, null,
                $"Could not reach the debug session (owner pid {ownerPid}): {ex.Message}");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

using System.Text.Json;
using RoslynMCP.Debugger;

namespace RoslynMCP.DebugWorker;

/// <summary>
/// Hosts a <see cref="DebugSession"/> in a process whose bitness matches the debug target, driven
/// by the host over line-delimited JSON on stdio.
/// </summary>
/// <remarks>
/// Exists only because ICorDebug cannot attach across x86/x64. The engine is the same class the
/// host runs in-process, so the two paths cannot behave differently.
/// </remarks>
internal static class Program
{
    private static readonly Lock s_writeGate = new();
    private static DebugSession? s_session;

    private static async Task<int> Main(string[] args)
    {
        var sessionId = args.Length > 0 && uint.TryParse(args[0], out var parsed) ? parsed : 1u;
        s_session = new DebugSession(sessionId);

        // Events flow up unsolicited, interleaved with responses on the same stream.
        var pump = Task.Run(async () =>
        {
            await foreach (var debugEvent in s_session.Events.ReadAllAsync())
                Write(new WorkerResponse { Ok = true, Event = debugEvent });
        });

        try
        {
            while (await Console.In.ReadLineAsync() is { } line)
            {
                if (line.Length == 0)
                    continue;

                WorkerRequest? request;
                try
                {
                    request = JsonSerializer.Deserialize<WorkerRequest>(line, WorkerProtocol.Options);
                }
                catch (JsonException ex)
                {
                    Console.Error.WriteLine($"malformed request: {ex.Message}");
                    continue;
                }

                if (request is null)
                    continue;

                var response = await HandleAsync(request);
                Write(response);

                if (request.Op == "terminate")
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"worker failed: {ex}");
            return 1;
        }
        finally
        {
            try { s_session.Terminate(); } catch { }
            await Task.WhenAny(pump, Task.Delay(TimeSpan.FromSeconds(2)));
        }

        return 0;
    }

    private static async Task<WorkerResponse> HandleAsync(WorkerRequest request)
    {
        var session = s_session!;
        var response = new WorkerResponse { Id = request.Id, Ok = true };

        try
        {
            switch (request.Op)
            {
                case "attach":
                    session.Attach(request.Pid, request.Breakpoints ?? [], request.Runtime);
                    break;

                case "addBreakpoint":
                    if (request.Breakpoint is null)
                        throw new ArgumentException("no breakpoint supplied");
                    session.AddBreakpoint(request.Breakpoint);
                    break;

                case "removeBreakpoint":
                    response.Removed = session.RemoveBreakpoint(request.FilePath ?? "", request.Line);
                    break;

                case "continue":
                    session.Continue();
                    break;

                case "step":
                    session.Step(request.Step);
                    break;

                case "stackTrace":
                    response.Frames = await session.StackTraceAsync();
                    break;

                case "variables":
                    response.Variables = await session.VariablesAsync(request.FrameIndex);
                    break;

                case "evaluate":
                {
                    var (ok, value, error) = await session.EvaluateAsync(request.FrameIndex, request.Expression ?? "");
                    response.Ok = ok;
                    response.Value = value;
                    response.Error = error;
                    break;
                }

                case "setVariable":
                {
                    var (ok, variable, error) = await session.SetVariableAsync(
                        request.FrameIndex, request.Name ?? "", request.Value ?? "");
                    response.Ok = ok;
                    response.Variable = variable;
                    response.Error = error;
                    break;
                }

                case "terminate":
                    session.Terminate();
                    break;

                default:
                    response.Ok = false;
                    response.Error = $"unknown operation '{request.Op}'";
                    break;
            }
        }
        catch (Exception ex)
        {
            response.Ok = false;
            response.Error = ex.Message;
        }

        return response;
    }

    private static void Write(WorkerResponse response)
    {
        var line = JsonSerializer.Serialize(response, WorkerProtocol.Options);
        lock (s_writeGate)
        {
            Console.Out.WriteLine(line);
            Console.Out.Flush();
        }
    }
}

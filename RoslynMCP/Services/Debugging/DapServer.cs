using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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
    private bool _running = true;

    /// <summary>DAP frame ids are opaque; the backend's are stack indices, so scopes carry the
    /// frame in their reference and variable references pass through untouched.</summary>
    private const int ScopeBase = 1;
    private const int ScopeLimit = 1000;

    public DapServer(IDebugBackend backend, Stream input, Stream output)
    {
        _backend = backend;
        _input = input;
        _output = output;
    }

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        // Attaching is the supported entry: launching under ICorDebug would mean driving the
        // target's process tree, which the engine has no notion of.
        using var backend = new PublishingDebugBackend(new IcorDebugBackend());
        var server = new DapServer(
            backend, Console.OpenStandardInput(), Console.OpenStandardOutput());

        await server.ListenAsync(ct);
        return 0;
    }

    public async Task ListenAsync(CancellationToken ct)
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

                case "attach":
                {
                    int pid = arguments?["processId"]?.GetValue<int>() ?? 0;
                    if (pid <= 0)
                    {
                        await RespondAsync(message, null, false, "No process id to attach to.");
                        break;
                    }
                    string result = await _backend.AttachToProcessAsync(pid, null, ct);
                    bool ok = !result.StartsWith("Error", StringComparison.OrdinalIgnoreCase);
                    await RespondAsync(message, null, ok, ok ? null : result);
                    if (!ok)
                        await EventAsync("terminated", null);
                    break;
                }

                case "configurationDone":
                    await RespondAsync(message, null);
                    break;

                case "setBreakpoints":
                {
                    string? file = arguments?["source"]?["path"]?.GetValue<string>();
                    var wanted = arguments?["breakpoints"]?.AsArray() ?? [];
                    var verified = new JsonArray();

                    foreach (var breakpoint in wanted)
                    {
                        int line = breakpoint?["line"]?.GetValue<int>() ?? 0;
                        var (_, id) = await _backend.SetBreakpointAsync(
                            file ?? "", line,
                            breakpoint?["condition"]?.GetValue<string>(),
                            breakpoint?["hitCondition"]?.GetValue<string>(),
                            breakpoint?["logMessage"]?.GetValue<string>(),
                            ct);

                        verified.Add(new JsonObject
                        {
                            ["verified"] = id is not null,
                            ["line"] = line,
                        });
                    }
                    await RespondAsync(message, new JsonObject { ["breakpoints"] = verified });
                    break;
                }

                case "setExceptionBreakpoints":
                {
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
                            entry["source"] = new JsonObject
                            {
                                ["name"] = Path.GetFileName(frame.FilePath),
                                ["path"] = frame.FilePath,
                            };
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
                    var (ok, stored, error) = await _backend.SetVariableAsync(
                        arguments?["name"]?.GetValue<string>() ?? "",
                        arguments?["value"]?.GetValue<string>() ?? "",
                        0, ct);

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
                    break;
                }

                case "pause":
                {
                    string result = await _backend.InterruptAsync(ct);
                    bool ok = !result.StartsWith("Error", StringComparison.OrdinalIgnoreCase);
                    await RespondAsync(message, null, ok, ok ? null : result);
                    if (ok)
                        await ReportStopAsync("pause");
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

                case "disconnect":
                case "terminate":
                    _backend.Stop();
                    await RespondAsync(message, null);
                    await EventAsync("terminated", null);
                    _running = false;
                    break;

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

    private static JsonObject Capabilities() => new()
    {
        ["supportsConfigurationDoneRequest"] = true,
        ["supportsSetVariable"] = true,
        ["supportsConditionalBreakpoints"] = true,
        ["supportsExceptionInfoRequest"] = true,
        ["supportsTerminateRequest"] = true,
        ["supportsEvaluateForHovers"] = true,
        // Both are emulated by PublishingDebugBackend rather than by the engine.
        ["supportsHitConditionalBreakpoints"] = true,
        ["supportsLogPoints"] = true,
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
            await EventAsync("terminated", null);
            _running = false;
            return;
        }

        await EventAsync("stopped", new JsonObject
        {
            ["reason"] = frame.ExceptionName is { Length: > 0 } ? "exception" : defaultReason,
            ["threadId"] = 1,
            ["allThreadsStopped"] = true,
            ["text"] = frame.ExceptionMessage,
        });
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

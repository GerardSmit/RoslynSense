using System.Text;
using System.Text.Json.Nodes;
using RoslynMCP.Services;
using RoslynMCP.Services.Debugging;

namespace RoslynMCP.Tests;

/// <summary>
/// A backend that records what it was asked to do. Enough to prove a command reaches the engine
/// with the arguments it was given, which is the failure mode these commands actually had.
/// </summary>
internal sealed class RecordingBackend : IDebugBackend
{
    public DebuggerService.StoppedFrame? Frame;
    public IReadOnlyList<ModuleInfo> Modules = [];

    public string? LastFile;
    public int LastLine;
    public bool Detached;
    public bool Stopped;

    public DebuggerService.StoppedFrame? CurrentFrame => Frame;

    public Task<string> RunToLocationAsync(
        string filePath, int line, CancellationToken cancellationToken = default)
    {
        LastFile = filePath;
        LastLine = line;
        return Task.FromResult("stopped at the cursor");
    }

    public Task<string> SetNextStatementAsync(
        string filePath, int line, CancellationToken cancellationToken = default)
    {
        LastFile = filePath;
        LastLine = line;
        return Task.FromResult("the next statement moved");
    }

    public Task<IReadOnlyList<ModuleInfo>> GetModulesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Modules);

    public Task<string> DetachAsync(CancellationToken cancellationToken = default)
    {
        Detached = true;
        return Task.FromResult("Detached. The process is still running.");
    }

    public string Stop()
    {
        Stopped = true;
        return "stopped";
    }

    // --- the rest of the contract, inert ---

    public Task<string> StartTestSessionAsync(string csprojPath, string? filter,
        IEnumerable<(string file, int line)>? initialBreakpoints = null,
        CancellationToken cancellationToken = default) => Task.FromResult("started");

    public Task<string> AttachToProcessAsync(int pid,
        IEnumerable<(string file, int line)>? initialBreakpoints = null,
        CancellationToken cancellationToken = default) => Task.FromResult("attached");

    public Task<(string Message, int? BreakpointId)> SetBreakpointAsync(
        string filePath, int line, string? condition = null, string? hitCondition = null,
        string? logMessage = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(("set", (int?)1));

    public Task<string> RemoveBreakpointAsync(int breakpointId, CancellationToken cancellationToken = default) =>
        Task.FromResult("removed");

    public Task<string> ContinueAsync(CancellationToken cancellationToken = default) => Task.FromResult("continued");
    public Task<string> StepInAsync(CancellationToken cancellationToken = default) => Task.FromResult("stepped");
    public Task<string> StepOverAsync(CancellationToken cancellationToken = default) => Task.FromResult("stepped");
    public Task<string> StepOutAsync(CancellationToken cancellationToken = default) => Task.FromResult("stepped");

    public Task<string> EvaluateAsync(string expression, CancellationToken cancellationToken = default) =>
        Task.FromResult("value");
    public Task<string> GetLocalsAsync(CancellationToken cancellationToken = default) => Task.FromResult("locals");
    public Task<string> GetStackTraceAsync(CancellationToken cancellationToken = default) => Task.FromResult("stack");
    public Task<string> InterruptAsync(CancellationToken cancellationToken = default) => Task.FromResult("paused");

    public Task<IReadOnlyList<StackFrameInfo>> GetStackFramesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StackFrameInfo>>([]);

    public Task<IReadOnlyList<VariableInfo>> GetVariablesAsync(
        int frameId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<VariableInfo>>([]);

    public Task<IReadOnlyList<VariableInfo>> GetVariableChildrenAsync(
        int variablesReference, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<VariableInfo>>([]);

    public Task<(bool Ok, string Value, string Error)> SetVariableAsync(
        string name, string value, int frameId = 0, CancellationToken cancellationToken = default) =>
        Task.FromResult((true, value, ""));

    public Task<string> SelectFrameAsync(int frameId, CancellationToken cancellationToken = default) =>
        Task.FromResult("selected");

    public Task<IReadOnlyList<ThreadInfo>> GetThreadsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ThreadInfo>>([]);

    public Task<ExceptionDetail?> GetExceptionInfoAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<ExceptionDetail?>(null);

    public Task<string> SetExceptionFiltersAsync(
        ExceptionFilters filters, CancellationToken cancellationToken = default) => Task.FromResult("ok");

    public string GetStatus() => "status";
    public void Dispose() { }
}

/// <summary>Drives a <see cref="DapServer"/> through its real wire format.</summary>
internal static class DapConversation
{
    public static JsonObject Request(int seq, string command, JsonObject? arguments = null)
    {
        var request = new JsonObject
        {
            ["seq"] = seq,
            ["type"] = "request",
            ["command"] = command,
        };
        if (arguments is not null)
            request["arguments"] = arguments;
        return request;
    }

    public static async Task<List<JsonNode>> RunAsync(
        IDebugBackend backend, IEnumerable<JsonObject> requests)
    {
        var input = new MemoryStream();
        foreach (var request in requests)
        {
            byte[] payload = Encoding.UTF8.GetBytes(request.ToJsonString());
            input.Write(Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n"));
            input.Write(payload);
        }
        input.Position = 0;

        var output = new MemoryStream();
        var server = new DapServer(backend, input, output);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await server.ListenAsync(timeout.Token);

        // Handlers are dispatched without awaiting, so the last of them needs a moment to write.
        for (int attempt = 0; attempt < 50 && output.Length == 0; attempt++)
            await Task.Delay(20, timeout.Token);
        await Task.Delay(150, timeout.Token);

        return Parse(output.ToArray());
    }

    private static List<JsonNode> Parse(byte[] raw)
    {
        var messages = new List<JsonNode>();
        string text = Encoding.UTF8.GetString(raw);
        int index = 0;

        while (true)
        {
            int headerEnd = text.IndexOf("\r\n\r\n", index, StringComparison.Ordinal);
            if (headerEnd < 0)
                break;

            int length = int.Parse(text[index..headerEnd]["Content-Length:".Length..].Trim());
            int bodyStart = headerEnd + 4;

            messages.Add(JsonNode.Parse(text[bodyStart..(bodyStart + length)])!);
            index = bodyStart + length;
        }
        return messages;
    }
}

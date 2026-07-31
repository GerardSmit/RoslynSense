using System.Text;
using System.Text.Json.Nodes;
using RoslynMCP.Services;
using RoslynMCP.Services.Debugging;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The .NET Framework debug adapter. netcoredbg speaks DAP but only to CoreCLR, so this is what
/// gives the editor F5 on a Framework project rather than "use the AI session".
/// </summary>
public class DapServerTests
{
    [Fact]
    public async Task InitializeAdvertisesWhatTheBackendCanActuallyDo()
    {
        var responses = await ConverseAsync(new FakeBackend(), [
            Request(1, "initialize"),
        ]);

        var capabilities = responses.Single(r => r["command"]?.GetValue<string>() == "initialize")["body"];

        Assert.True(capabilities!["supportsSetVariable"]!.GetValue<bool>());
        Assert.True(capabilities["supportsExceptionInfoRequest"]!.GetValue<bool>());
        // Emulated a layer up rather than by the engine, but true from the client's side.
        Assert.True(capabilities["supportsHitConditionalBreakpoints"]!.GetValue<bool>());
        Assert.True(capabilities["supportsLogPoints"]!.GetValue<bool>());
        Assert.True(capabilities["supportsDataBreakpoints"]!.GetValue<bool>());
    }

    [Fact]
    public async Task DataBreakpointInfoOffersWriteOnlyAndAnIdThatCarriesTheFrame()
    {
        var backend = new FakeBackend
        {
            Frame = new DebuggerService.StoppedFrame("breakpoint-hit", "Order.Total", @"C:\src\Order.cs", 42, 1),
        };

        var responses = await ConverseAsync(backend, [
            Request(1, "dataBreakpointInfo", new JsonObject { ["name"] = "total", ["frameId"] = 2 }),
        ]);

        var body = responses.Single(r => r["command"]?.GetValue<string>() == "dataBreakpointInfo")["body"];

        Assert.Equal("2:total", body!["dataId"]!.GetValue<string>());
        Assert.Equal("write", body["accessTypes"]!.AsArray().Single()!.GetValue<string>());
    }

    [Fact]
    public async Task DataBreakpointInfoRefusesWhileTheTargetIsRunning()
    {
        // The expression is evaluated in the current frame, so there has to be one.
        var responses = await ConverseAsync(new FakeBackend(), [
            Request(1, "dataBreakpointInfo", new JsonObject { ["name"] = "total" }),
        ]);

        var body = responses.Single(r => r["command"]?.GetValue<string>() == "dataBreakpointInfo")["body"];

        Assert.Null(body!["dataId"]?.GetValue<string>());
    }

    [Fact]
    public async Task InitializeIsFollowedByTheInitializedEvent()
    {
        var messages = await ConverseAsync(new FakeBackend(), [Request(1, "initialize")]);

        Assert.Contains(messages, m =>
            m["type"]?.GetValue<string>() == "event" && m["event"]?.GetValue<string>() == "initialized");
    }

    [Fact]
    public async Task StackFramesCarryTheirSourceSoTheyAreClickable()
    {
        var backend = new FakeBackend
        {
            Frames =
            [
                new StackFrameInfo(0, "Order.Total", @"C:\src\Order.cs", 42, 9, false),
                new StackFrameInfo(1, "[Native Frames]", "", 0, 0, true),
            ],
        };

        var responses = await ConverseAsync(backend, [Request(1, "stackTrace")]);
        var frames = responses.Single()["body"]!["stackFrames"]!.AsArray();

        Assert.Equal(2, frames.Count);
        Assert.Equal(@"C:\src\Order.cs", frames[0]!["source"]!["path"]!.GetValue<string>());
        Assert.Equal(42, frames[0]!["line"]!.GetValue<int>());
        Assert.Equal("subtle", frames[1]!["presentationHint"]!.GetValue<string>());
        // A frame with no file must not claim one.
        Assert.Null(frames[1]!["source"]);
    }

    [Fact]
    public async Task ScopesAddressTheirFrameSoACallersLocalsCanBeRead()
    {
        var backend = new FakeBackend();

        var responses = await ConverseAsync(backend, [
            Request(1, "scopes", new JsonObject { ["frameId"] = 2 }),
        ]);

        int reference = responses.Single()["body"]!["scopes"]!.AsArray()[0]!["variablesReference"]!.GetValue<int>();

        // Reading that scope must reach frame 2, not frame 0.
        var variables = await ConverseAsync(backend, [
            Request(1, "variables", new JsonObject { ["variablesReference"] = reference }),
        ]);

        Assert.Single(variables);
        Assert.Equal(2, backend.LastRequestedFrame);
    }

    [Fact]
    public async Task AVariableReferenceIsPassedThroughToTheBackendUnchanged()
    {
        var backend = new FakeBackend();

        await ConverseAsync(backend, [
            Request(1, "variables", new JsonObject { ["variablesReference"] = 1500 }),
        ]);

        Assert.Equal(1500, backend.LastChildReference);
    }

    [Fact]
    public async Task ContinueReportsTheStopThatEndedIt()
    {
        var backend = new FakeBackend
        {
            Frame = new DebuggerService.StoppedFrame("breakpoint-hit", "Order.Total", @"C:\src\Order.cs", 42, 1),
        };

        var messages = await ConverseAsync(backend, [Request(1, "continue")]);

        Assert.Contains(messages, m => m["event"]?.GetValue<string>() == "continued");
        var stopped = messages.Single(m => m["event"]?.GetValue<string>() == "stopped");
        Assert.Equal("breakpoint", stopped["body"]!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task AStopWithNoFrameEndsTheSession()
    {
        // The target ran to completion; reporting a stop no one can inspect would leave the UI
        // waiting on a dead process.
        var messages = await ConverseAsync(new FakeBackend { Frame = null }, [Request(1, "continue")]);

        Assert.Contains(messages, m => m["event"]?.GetValue<string>() == "terminated");
    }

    [Fact]
    public async Task AnExceptionStopIsReportedAsOne()
    {
        var backend = new FakeBackend
        {
            Frame = new DebuggerService.StoppedFrame(
                "exception", "Order.Total", @"C:\src\Order.cs", 42, 0,
                ExceptionName: "System.InvalidOperationException",
                ExceptionMessage: "Sequence contains no elements"),
        };

        var messages = await ConverseAsync(backend, [Request(1, "continue")]);
        var stopped = messages.Single(m => m["event"]?.GetValue<string>() == "stopped");

        Assert.Equal("exception", stopped["body"]!["reason"]!.GetValue<string>());
        Assert.Equal("Sequence contains no elements", stopped["body"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task BreakpointsCarryTheirHitConditionAndLogMessage()
    {
        var backend = new FakeBackend();

        await ConverseAsync(backend, [
            Request(1, "setBreakpoints", new JsonObject
            {
                ["source"] = new JsonObject { ["path"] = @"C:\src\Order.cs" },
                ["breakpoints"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["line"] = 42,
                        ["hitCondition"] = ">= 3",
                        ["logMessage"] = "total={total}",
                    },
                },
            }),
        ]);

        Assert.Equal(">= 3", backend.LastHitCondition);
        Assert.Equal("total={total}", backend.LastLogMessage);
    }

    [Fact]
    public async Task AnUnknownRequestFailsByNameRatherThanSilently()
    {
        var responses = await ConverseAsync(new FakeBackend(), [Request(1, "reverseTime")]);

        var response = responses.Single(m => m["type"]?.GetValue<string>() == "response");
        Assert.False(response["success"]!.GetValue<bool>());
        Assert.Contains("reverseTime", response["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task DisconnectStopsTheBackendAndEndsTheSession()
    {
        var backend = new FakeBackend();

        var messages = await ConverseAsync(backend, [Request(1, "disconnect")]);

        Assert.True(backend.Stopped);
        Assert.Contains(messages, m => m["event"]?.GetValue<string>() == "terminated");
    }

    // --- Harness ---

    private static JsonObject Request(int seq, string command, JsonObject? arguments = null)
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

    /// <summary>Feeds requests through the wire format and reads back everything sent.</summary>
    private static async Task<List<JsonNode>> ConverseAsync(
        IDebugBackend backend, IEnumerable<JsonObject> requests)
    {
        var input = new MemoryStream();
        foreach (var request in requests)
        {
            byte[] payload = Encoding.UTF8.GetBytes(request.ToJsonString());
            byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
            input.Write(header);
            input.Write(payload);
        }
        input.Position = 0;

        var output = new MemoryStream();
        var server = new DapServer(backend, input, output);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await server.ListenAsync(timeout.Token);

        // Handlers are dispatched without awaiting, so give the last of them a moment to write.
        for (int attempt = 0; attempt < 50 && output.Length == 0; attempt++)
            await Task.Delay(20, timeout.Token);
        await Task.Delay(100, timeout.Token);

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

            string header = text[index..headerEnd];
            int length = int.Parse(header["Content-Length:".Length..].Trim());
            int bodyStart = headerEnd + 4;

            messages.Add(JsonNode.Parse(text[bodyStart..(bodyStart + length)])!);
            index = bodyStart + length;
        }
        return messages;
    }

    private sealed class FakeBackend : IDebugBackend
    {
        public DebuggerService.StoppedFrame? Frame;
        public IReadOnlyList<StackFrameInfo> Frames = [];
        public int LastRequestedFrame = -1;
        public int LastChildReference = -1;
        public string? LastHitCondition;
        public string? LastLogMessage;
        public bool Stopped;

        public DebuggerService.StoppedFrame? CurrentFrame => Frame;

        public Task<string> StartTestSessionAsync(string csprojPath, string? filter,
            IEnumerable<(string file, int line)>? initialBreakpoints = null,
            CancellationToken cancellationToken = default) => Task.FromResult("started");

        public Task<string> AttachToProcessAsync(int pid,
            IEnumerable<(string file, int line)>? initialBreakpoints = null,
            CancellationToken cancellationToken = default) => Task.FromResult("attached");

        public Task<(string Message, int? BreakpointId)> SetBreakpointAsync(
            string filePath, int line, string? condition = null, string? hitCondition = null,
            string? logMessage = null, CancellationToken cancellationToken = default)
        {
            LastHitCondition = hitCondition;
            LastLogMessage = logMessage;
            return Task.FromResult(("set", (int?)1));
        }

        public Task<string> RemoveBreakpointAsync(int breakpointId, CancellationToken cancellationToken = default) =>
            Task.FromResult("removed");

        public Task<string> ContinueAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("continued");
        public Task<string> StepInAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("stepped");
        public Task<string> StepOverAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("stepped");
        public Task<string> StepOutAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("stepped");

        public Task<string> EvaluateAsync(string expression, CancellationToken cancellationToken = default) =>
            Task.FromResult($"eval:{expression}");
        public Task<string> GetLocalsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("locals");
        public Task<string> GetStackTraceAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("stack");
        public Task<string> InterruptAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("interrupted");

        public Task<IReadOnlyList<StackFrameInfo>> GetStackFramesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Frames);

        public Task<IReadOnlyList<VariableInfo>> GetVariablesAsync(
            int frameId, CancellationToken cancellationToken = default)
        {
            LastRequestedFrame = frameId;
            return Task.FromResult<IReadOnlyList<VariableInfo>>(
                [new VariableInfo("total", "42", "int", 0, 0, 0, true)]);
        }

        public Task<IReadOnlyList<VariableInfo>> GetVariableChildrenAsync(
            int variablesReference, CancellationToken cancellationToken = default)
        {
            LastChildReference = variablesReference;
            return Task.FromResult<IReadOnlyList<VariableInfo>>([]);
        }

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
            ExceptionFilters filters, CancellationToken cancellationToken = default) =>
            Task.FromResult("ok");

        public Task<string> RunToLocationAsync(
            string filePath, int line, CancellationToken cancellationToken = default) =>
            Task.FromResult("ran to location");

        public Task<string> SetNextStatementAsync(
            string filePath, int line, CancellationToken cancellationToken = default) =>
            Task.FromResult("moved");

        public Task<IReadOnlyList<ModuleInfo>> GetModulesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModuleInfo>>([]);

        public Task<string> DetachAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("detached");

        public string GetStatus() => "status";

        public string Stop()
        {
            Stopped = true;
            return "stopped";
        }

        public void Dispose() { }
    }
}

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
    public async Task EngineDiagnosticsReachTheDebugConsole()
    {
        // The engine has always said why a breakpoint cannot bind; nothing forwarded it, so the
        // Debug Console stayed empty and the only symptom was a breakpoint that never hit.
        var backend = new NoticeBackend();
        backend.OnAttach = () => backend.Raise(new DebugNotice(
            DebugNoticeKind.Diagnostic,
            "no symbols for WebFormsApp.dll — source breakpoints in it cannot bind"));

        var messages = await ConverseUntilAsync(
            backend,
            [Request(1, "attach", new JsonObject { ["processId"] = 4242 })],
            m => m.Any(x => x["event"]?.GetValue<string>() == "output"));

        var body = messages.Single(m => m["event"]?.GetValue<string>() == "output")["body"]!;

        Assert.Equal("console", body["category"]!.GetValue<string>());
        Assert.Contains("no symbols", body["output"]!.GetValue<string>());
    }

    /// <summary>
    /// The editor registers a running app from this event; a .NET Framework session that never
    /// sends one is invisible to the process registry, and so to every chat.
    /// </summary>
    [Fact]
    public async Task TheSessionReportsWhichProcessItDebugs()
    {
        var messages = await ConverseUntilAsync(
            new NoticeBackend(),
            [Request(1, "attach", new JsonObject { ["processId"] = 4242 })],
            m => m.Any(x => x["event"]?.GetValue<string>() == "process"));

        var body = messages.Single(m => m["event"]?.GetValue<string>() == "process")["body"]!;

        Assert.Equal(4242, body["systemProcessId"]!.GetValue<int>());
        Assert.Equal("attach", body["startMethod"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheDebuggeesOwnOutputIsNotLabelledAsTheAdaptersOwn()
    {
        var backend = new NoticeBackend();
        backend.OnAttach = () => backend.Raise(new DebugNotice(DebugNoticeKind.Output, "hello"));

        var messages = await ConverseUntilAsync(
            backend,
            [Request(1, "attach", new JsonObject { ["processId"] = 4242 })],
            m => m.Any(x => x["event"]?.GetValue<string>() == "output"));

        var body = messages.Single(m => m["event"]?.GetValue<string>() == "output")["body"]!;

        Assert.Equal("stdout", body["category"]!.GetValue<string>());
        Assert.Equal("hello\n", body["output"]!.GetValue<string>());
    }

    [Fact]
    public async Task ABreakpointTheEngineHasNotBoundIsReportedAsPending()
    {
        // Accepting a breakpoint is not binding one: its module may not have loaded, or may have
        // shipped without a PDB. Reporting acceptance as verification drew a solid red dot on a
        // breakpoint that could never fire.
        var messages = await ConverseUntilAsync(
            new NoticeBackend(),
            [
                Request(1, "configurationDone"),
                Request(2, "setBreakpoints", new JsonObject
                {
                    ["source"] = new JsonObject { ["path"] = @"C:\src\Default.aspx.cs" },
                    ["breakpoints"] = new JsonArray { new JsonObject { ["line"] = 42 } },
                }),
            ],
            m => m.Any(x => x["command"]?.GetValue<string>() == "setBreakpoints"));

        var answered = messages
            .Single(m => m["command"]?.GetValue<string>() == "setBreakpoints")["body"]!["breakpoints"]!
            .AsArray()[0]!;

        Assert.False(answered["verified"]!.GetValue<bool>());
        Assert.Equal(42, answered["line"]!.GetValue<int>());
        Assert.Contains("module", answered["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task BindingTellsTheClientWhereTheBreakpointActuallyLanded()
    {
        var backend = new NoticeBackend();
        backend.OnSetBreakpoint = () => backend.Raise(new DebugNotice(
            DebugNoticeKind.BreakpointBound, "bound at line 45 in WebFormsApp.dll",
            @"C:\src\Default.aspx.cs", 45, 1));

        var messages = await ConverseUntilAsync(
            backend,
            [
                Request(1, "configurationDone"),
                Request(2, "setBreakpoints", new JsonObject
                {
                    ["source"] = new JsonObject { ["path"] = @"C:\src\Default.aspx.cs" },
                    ["breakpoints"] = new JsonArray { new JsonObject { ["line"] = 42 } },
                }),
            ],
            m => m.Any(x => x["event"]?.GetValue<string>() == "breakpoint"));

        var bound = messages
            .Single(m => m["event"]?.GetValue<string>() == "breakpoint")["body"]!["breakpoint"]!;

        Assert.Equal(1, bound["id"]!.GetValue<int>());
        Assert.True(bound["verified"]!.GetValue<bool>());
        // Bound to the nearest sequence point, which is not always the line that was asked for.
        Assert.Equal(45, bound["line"]!.GetValue<int>());
    }

    [Fact]
    public async Task EditingABreakpointsConditionDoesNotDestroyIt()
    {
        // The engine keys breakpoints by file and line. Setting the replacement before removing
        // the original meant the replacement could not bind, and removing the original then took
        // the replacement with it — leaving the line with no breakpoint and nothing pending.
        var backend = new NoticeBackend();
        var source = new JsonObject { ["path"] = @"C:\src\Order.cs" };

        await ConverseUntilAsync(
            backend,
            [
                Request(1, "configurationDone"),
                Request(2, "setBreakpoints", new JsonObject
                {
                    ["source"] = source.DeepClone(),
                    ["breakpoints"] = new JsonArray { new JsonObject { ["line"] = 42 } },
                }),
                Request(3, "setBreakpoints", new JsonObject
                {
                    ["source"] = source.DeepClone(),
                    ["breakpoints"] = new JsonArray
                    {
                        new JsonObject { ["line"] = 42, ["condition"] = "total > 10" },
                    },
                }),
            ],
            m => m.Count(x => x["command"]?.GetValue<string>() == "setBreakpoints") == 2);

        Assert.Equal("total > 10", backend.LastCondition);
        // The old one goes before the new one is set, so the engine is never asked to hold two
        // breakpoints on one line.
        Assert.Equal([42, 0, 42], backend.BreakpointCalls);
    }

    [Fact]
    public async Task AStopNobodyAskedForIsStillReported()
    {
        // The case F5 on a web app is made of: nothing is outstanding, a request comes in and hits
        // a breakpoint. The adapter only ever reported stops that ended a resume it had issued, so
        // the site hung suspended and the editor showed no stop at all.
        var backend = new NoticeBackend
        {
            Frame = new DebuggerService.StoppedFrame(
                "breakpoint", "Default.Page_Load", @"C:\src\Default.aspx.cs", 45, 1),
        };
        backend.OnAttach = () => backend.Raise(new DebugNotice(
            DebugNoticeKind.Stopped, "breakpoint", @"C:\src\Default.aspx.cs", 45));

        var messages = await ConverseUntilAsync(
            backend,
            [Request(1, "attach", new JsonObject { ["processId"] = 4242 })],
            m => m.Any(x => x["event"]?.GetValue<string>() == "stopped"));

        var stopped = messages.Single(m => m["event"]?.GetValue<string>() == "stopped")["body"]!;

        Assert.Equal("breakpoint", stopped["reason"]!.GetValue<string>());
        Assert.True(stopped["allThreadsStopped"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AStopIsAnnouncedOnceThoughBothRoutesReportIt()
    {
        var backend = new NoticeBackend
        {
            Frame = new DebuggerService.StoppedFrame(
                "breakpoint", "Cart.Total", @"C:\src\Default.aspx.cs", 17, 1),
        };
        // One stop, two reporters: the engine announces it, and the resume it ended returns and
        // announces it too. Sent twice, the editor re-entered the state it had just left — which
        // left the stopped line highlighted after Continue.
        backend.OnContinue = () => backend.Raise(new DebugNotice(
            DebugNoticeKind.Stopped, "breakpoint", @"C:\src\Default.aspx.cs", 17));

        var messages = await ConverseUntilAsync(
            backend,
            [Request(1, "continue")],
            m => m.Any(x => x["event"]?.GetValue<string>() == "stopped"));

        Assert.Equal(1, messages.Count(m => m["event"]?.GetValue<string>() == "stopped"));
    }

    [Fact]
    public async Task AResumeAnotherClientIssuedIsAnnouncedAsContinued()
    {
        // The chat can resume a session this editor is attached to (over the command pipe). The
        // stop it left was already pushed; without the matching continued the editor kept showing
        // that stale stop until the next one arrived — and forever if none did.
        var backend = new NoticeBackend { Frame = null };
        backend.OnAttach = () => backend.Raise(new DebugNotice(DebugNoticeKind.Resumed, ""));

        var messages = await ConverseUntilAsync(
            backend,
            [Request(1, "attach", new JsonObject { ["processId"] = 4242 })],
            m => m.Any(x => x["event"]?.GetValue<string>() == "continued"));

        var continued = messages.Single(m => m["event"]?.GetValue<string>() == "continued")["body"]!;
        Assert.True(continued["allThreadsContinued"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AResumeThatAlreadyLandedIsNotAnnounced()
    {
        // By the time the pump reads the resume notice the target has stopped again; that stop's
        // own notice is behind it in the queue, and a late continued would overwrite the stop.
        var backend = new NoticeBackend
        {
            Frame = new DebuggerService.StoppedFrame(
                "breakpoint", "Cart.Total", @"C:\src\Default.aspx.cs", 17, 1),
        };
        backend.OnAttach = () => backend.Raise(new DebugNotice(DebugNoticeKind.Resumed, ""));

        var messages = await ConverseUntilAsync(
            backend,
            [Request(1, "attach", new JsonObject { ["processId"] = 4242 })],
            m => m.Any(x => x["command"]?.GetValue<string>() == "attach"));

        Assert.DoesNotContain(messages, m => m["event"]?.GetValue<string>() == "continued");
    }

    [Fact]
    public async Task ARunningTargetThatNeverStopsAgainKeepsItsSession()
    {
        // Continuing a web request that hits nothing else is the ordinary case: the resume gives
        // up waiting and there is no frame. Reading that as an exit ended the session a minute
        // after every such Continue.
        var backend = new NoticeBackend { Frame = null };

        var messages = await ConverseUntilAsync(
            backend,
            [Request(1, "continue")],
            m => m.Any(x => x["command"]?.GetValue<string>() == "continue"));

        Assert.DoesNotContain(messages, m => m["event"]?.GetValue<string>() == "terminated");
    }

    [Fact]
    public async Task ADebuggeeThatEndsOnItsOwnEndsTheSession()
    {
        var backend = new NoticeBackend();
        backend.OnAttach = () => backend.Raise(new DebugNotice(DebugNoticeKind.Exited, ""));

        var messages = await ConverseUntilAsync(
            backend,
            [Request(1, "attach", new JsonObject { ["processId"] = 4242 })],
            m => m.Any(x => x["event"]?.GetValue<string>() == "terminated"));

        Assert.Contains(messages, m => m["event"]?.GetValue<string>() == "exited");
    }

    [Fact]
    public async Task AnEngineThatReportsNoBindsStillVerifiesWhatItAccepted()
    {
        // netcoredbg answers binding itself; leaving its breakpoints unverified would draw every
        // one of them as pending for the whole session.
        var messages = await ConverseAsync(new FakeBackend(), [
            Request(1, "configurationDone"),
            Request(2, "setBreakpoints", new JsonObject
            {
                ["source"] = new JsonObject { ["path"] = @"C:\src\Order.cs" },
                ["breakpoints"] = new JsonArray { new JsonObject { ["line"] = 42 } },
            }),
        ]);

        var answered = messages
            .Single(m => m["command"]?.GetValue<string>() == "setBreakpoints")["body"]!["breakpoints"]!
            .AsArray()[0]!;

        Assert.True(answered["verified"]!.GetValue<bool>());
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
    public async Task DisconnectKillsADebuggeeThisSessionStarted()
    {
        var backend = new FakeBackend();

        var messages = await ConverseAsync(backend, [
            Request(1, "disconnect", new JsonObject { ["terminateDebuggee"] = true }),
        ]);

        Assert.True(backend.Stopped);
        Assert.Contains(messages, m => m["event"]?.GetValue<string>() == "terminated");
    }

    /// <summary>
    /// Disconnecting from a process that was only being inspected must leave it running — killing
    /// an attached IIS Express or w3wp worker takes the site down with the debug session.
    /// </summary>
    [Fact]
    public async Task DisconnectLeavesAnAttachedProcessRunning()
    {
        var backend = new FakeBackend();

        var messages = await ConverseAsync(backend, [
            Request(1, "attach", new JsonObject { ["processId"] = 4242 }),
            Request(2, "disconnect"),
        ]);

        Assert.True(backend.Detached);
        Assert.False(backend.Stopped);
        Assert.Contains(messages, m => m["event"]?.GetValue<string>() == "terminated");
    }

    /// <summary>
    /// The stop button must not read as a crash to the debuggee: it is asked to shut down, so
    /// hosted services get their StopAsync, rather than being killed where it stands.
    /// </summary>
    [Fact]
    public async Task TerminateAsksTheDebuggeeToShutDownRatherThanKillingIt()
    {
        var backend = new FakeBackend();

        var messages = await ConverseUntilAsync(
            backend,
            [Request(1, "terminate")],
            m => m.Any(x => x["event"]?.GetValue<string>() == "terminated"));

        Assert.True(backend.ShutdownRequested);
        Assert.False(backend.Stopped);

        var response = messages.Single(m => m["command"]?.GetValue<string>() == "terminate");
        Assert.True(response["success"]!.GetValue<bool>());
    }

    /// <summary>
    /// A debuggee that will not go still has to die: the editor's second stop press arrives as a
    /// disconnect, and it must not wait behind the shutdown it is overriding.
    /// </summary>
    [Fact]
    public async Task DisconnectDuringAShutdownKillsTheDebuggee()
    {
        var backend = new FakeBackend { ShutdownDelay = TimeSpan.FromSeconds(5) };

        await ConverseUntilAsync(
            backend,
            [
                Request(1, "terminate"),
                Request(2, "disconnect", new JsonObject { ["terminateDebuggee"] = true }),
            ],
            _ => backend.Stopped);

        // Killed while the shutdown it overrode is still running, rather than queued behind it.
        Assert.True(backend.Stopped);
        Assert.True(backend.ShutdownRequested);
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
        var input = new MemoryStream(Frame(requests));
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

    /// <summary>
    /// Feeds requests and holds the transport open until the adapter has sent what the test is
    /// waiting for.
    /// </summary>
    /// <remarks>
    /// Engine notices arrive on their own schedule rather than as a response, so a harness that
    /// closes the input as soon as the last request is read races them and observes nothing.
    /// </remarks>
    private static async Task<List<JsonNode>> ConverseUntilAsync(
        IDebugBackend backend, IEnumerable<JsonObject> requests, Func<List<JsonNode>, bool> until)
    {
        var input = new HeldStream(Frame(requests));
        var output = new CollectingStream();
        var server = new DapServer(backend, input, output);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // HeldStream deliberately blocks after the scripted requests. Run the listener on its own
        // worker so a synchronously completed ReadAsync cannot block this harness before it gets
        // the chance to observe the output and release the stream. That scheduling race only
        // showed up reliably on the hosted runner.
        var listening = Task.Run(() => server.ListenAsync(timeout.Token), CancellationToken.None);

        try
        {
            while (!until(Parse(output.Snapshot())))
            {
                if (timeout.IsCancellationRequested)
                    throw new TimeoutException("the adapter never sent what the test was waiting for");
                await Task.Delay(20, CancellationToken.None);
            }

            // Settle before snapshotting: a test asserting that something was sent once has to give a
            // duplicate the chance to arrive, or it passes by reading too early.
            await Task.Delay(150, CancellationToken.None);

            return Parse(output.Snapshot());
        }
        finally
        {
            // Release on both success and assertion/timeout failure. Otherwise the listener owns a
            // permanently blocked thread which can keep the test host alive until blame-hang kills it.
            input.Release();
            await listening.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private static byte[] Frame(IEnumerable<JsonObject> requests)
    {
        var stream = new MemoryStream();
        foreach (var request in requests)
        {
            byte[] payload = Encoding.UTF8.GetBytes(request.ToJsonString());
            stream.Write(Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n"));
            stream.Write(payload);
        }
        return stream.ToArray();
    }

    /// <summary>
    /// Reads framed messages out of the raw byte stream.
    /// </summary>
    /// <remarks>
    /// Byte offsets rather than string ones: Content-Length counts bytes, so a single non-ASCII
    /// character anywhere in a message desynchronises a reader that slices a decoded string. A
    /// message that is only half written is left for the next read.
    /// </remarks>
    private static List<JsonNode> Parse(byte[] raw)
    {
        var messages = new List<JsonNode>();
        int index = 0;

        while (index < raw.Length)
        {
            int headerEnd = HeaderEnd(raw, index);
            if (headerEnd < 0)
                break;

            string header = Encoding.ASCII.GetString(raw, index, headerEnd - index);
            int length = int.Parse(header["Content-Length:".Length..].Trim());
            int bodyStart = headerEnd + 4;
            if (bodyStart + length > raw.Length)
                break;

            messages.Add(JsonNode.Parse(Encoding.UTF8.GetString(raw, bodyStart, length))!);
            index = bodyStart + length;
        }
        return messages;
    }

    private static int HeaderEnd(byte[] raw, int from)
    {
        for (int i = from; i + 3 < raw.Length; i++)
        {
            if (raw[i] == '\r' && raw[i + 1] == '\n' && raw[i + 2] == '\r' && raw[i + 3] == '\n')
                return i;
        }
        return -1;
    }

    /// <summary>Replays a fixed script, then blocks instead of reporting end-of-stream until the
    /// test lets go.</summary>
    private sealed class HeldStream : Stream
    {
        private readonly byte[] _data;
        private readonly ManualResetEventSlim _released = new();
        private int _position;

        public HeldStream(byte[] data) => _data = data;

        public void Release() => _released.Set();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position < _data.Length)
            {
                int taken = Math.Min(count, _data.Length - _position);
                Array.Copy(_data, _position, buffer, offset, taken);
                _position += taken;
                return taken;
            }

            _released.Wait();
            return 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Collects what the adapter writes while the test reads it from another thread.</summary>
    private sealed class CollectingStream : Stream
    {
        private readonly List<byte> _written = [];

        public byte[] Snapshot()
        {
            lock (_written)
                return [.. _written];
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_written)
                _written.AddRange(buffer.AsSpan(offset, count).ToArray());
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length { get { lock (_written) return _written.Count; } }
        public override long Position { get => Length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private class FakeBackend : IDebugBackend
    {
        public DebuggerService.StoppedFrame? Frame;
        public IReadOnlyList<StackFrameInfo> Frames = [];
        public int LastRequestedFrame = -1;
        public int LastChildReference = -1;
        public string? LastHitCondition;
        public string? LastLogMessage;
        public string? LastCondition;
        public bool Stopped;
        public bool Detached;
        public bool ShutdownRequested;

        /// <summary>How long the debuggee takes to shut itself down, for the force path.</summary>
        public TimeSpan ShutdownDelay = TimeSpan.Zero;

        /// <summary>Every breakpoint call in order, as the line set or 0 for a removal.</summary>
        public readonly List<int> BreakpointCalls = [];

        /// <summary>Hooks for driving what the engine would report while the call is in flight.</summary>
        public Action? OnAttach;
        public Action? OnSetBreakpoint;
        public Action? OnContinue;

        public DebuggerService.StoppedFrame? CurrentFrame => Frame;

        public Task<string> StartTestSessionAsync(string csprojPath, string? filter,
            IEnumerable<(string file, int line)>? initialBreakpoints = null,
            CancellationToken cancellationToken = default) => Task.FromResult("started");

        public Task<string> AttachToProcessAsync(int pid,
            IEnumerable<(string file, int line)>? initialBreakpoints = null,
            CancellationToken cancellationToken = default)
        {
            OnAttach?.Invoke();
            return Task.FromResult("attached");
        }

        public Task<(string Message, int? BreakpointId)> SetBreakpointAsync(
            string filePath, int line, string? condition = null, string? hitCondition = null,
            string? logMessage = null, CancellationToken cancellationToken = default)
        {
            LastHitCondition = hitCondition;
            LastLogMessage = logMessage;
            LastCondition = condition;
            lock (BreakpointCalls)
                BreakpointCalls.Add(line);
            OnSetBreakpoint?.Invoke();
            return Task.FromResult(("set", (int?)1));
        }

        public Task<string> RemoveBreakpointAsync(int breakpointId, CancellationToken cancellationToken = default)
        {
            lock (BreakpointCalls)
                BreakpointCalls.Add(0);
            return Task.FromResult("removed");
        }

        public Task<string> ContinueAsync(CancellationToken cancellationToken = default)
        {
            OnContinue?.Invoke();
            return Task.FromResult("continued");
        }
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

        public Task<string> DetachAsync(CancellationToken cancellationToken = default)
        {
            Detached = true;
            return Task.FromResult("detached");
        }

        public async Task<(bool Graceful, string Message)> ShutdownAsync(
            TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            ShutdownRequested = true;
            if (ShutdownDelay > TimeSpan.Zero)
                await Task.Delay(ShutdownDelay, CancellationToken.None);
            return (true, "Debug session stopped; the debuggee shut down cleanly.");
        }

        public string GetStatus() => "status";

        public string Stop()
        {
            Stopped = true;
            return "stopped";
        }

        public void Dispose() { }
    }

    /// <summary>A backend that reports what happens between stops, as the ICorDebug engine does.</summary>
    private sealed class NoticeBackend : FakeBackend, IDebugNoticeSource
    {
        private long _stops;

        public event Action<DebugNotice>? Notice;

        public long StopSequence => Interlocked.Read(ref _stops);

        public void Raise(DebugNotice notice)
        {
            if (notice.Kind == DebugNoticeKind.Stopped)
                Interlocked.Increment(ref _stops);
            Notice?.Invoke(notice);
        }
    }
}

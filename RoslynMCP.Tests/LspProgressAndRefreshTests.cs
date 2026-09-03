using System.Collections.Concurrent;
using System.Text.Json;
using Nerdbank.Streams;
using RoslynMCP.Config;
using RoslynMCP.Daemon;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using StreamJsonRpc;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>Server-initiated traffic: $/progress for long operations, and the refresh nudges
/// that keep derived client data (diagnostics, lenses, hints) from going stale.</summary>
/// <remarks>
/// Serialized because <see cref="ProgressReporter.Factory"/> and the session registry are
/// process-wide: progress is broadcast to every attached session, so any other test that opens a
/// progress scope — a package audit, a bulk update — lands in this session's stream and is
/// indistinguishable from the one under test.
/// </remarks>
[Collection(SharedState.Name)]
public class LspProgressAndRefreshTests
{
    [Fact]
    public async Task LongOperationsReportBeginReportAndEndProgress()
    {
        await using var session = await EditorSession.StartAsync(new
        {
            processId = 1234,
            rootUri = (string?)null,
            capabilities = new { },
        });

        // Opened first, and deliberately: the stream a session reads is not its own, so an audit
        // or a restore another test started is in it too, ahead of anything this one does. Written
        // out rather than waited for, because a hazard only another test can supply is a hazard
        // that stops being tested the day that test is deleted.
        await using (var other = await ProgressReporter.BeginAsync("Some Other Operation"))
            other.Report("Not this one", 90);

        await using (var scope = await ProgressReporter.BeginAsync("Loading Sandbox"))
            scope.Report("Restoring packages", 40);

        await session.Client.WaitForAsync("begin", p => Title(p) == "Some Other Operation");

        // Found by its title and then followed by its token, rather than taken as the first event
        // of each kind — which would be the operation above every time.
        var begin = await session.Client.WaitForAsync(
            "begin", p => Title(p) == "Loading Sandbox");

        string token = begin.GetProperty("token").GetString()!;
        Assert.StartsWith("roslyn-sense/", token);

        await session.Client.WaitForAsync("create", Carrying(token));

        var report = await session.Client.WaitForAsync("report", Carrying(token));
        Assert.Equal("Restoring packages", report.GetProperty("value").GetProperty("message").GetString());
        Assert.Equal(40, report.GetProperty("value").GetProperty("percentage").GetInt32());

        await session.Client.WaitForAsync("end", Carrying(token));
    }

    [Fact]
    public async Task RefreshIsSentOnlyForCapabilitiesTheClientDeclared()
    {
        await using var session = await EditorSession.StartAsync(new
        {
            processId = 1234,
            rootUri = (string?)null,
            capabilities = new
            {
                textDocument = new { diagnostic = new { } },
                workspace = new { codeLens = new { refreshSupport = true } },
            },
        });

        await LspSessionRegistry.RequestRefreshAsync(RefreshKind.All);

        await session.Client.WaitForAsync("workspace/diagnostic/refresh");
        await session.Client.WaitForAsync("workspace/codeLens/refresh");

        // inlayHint refresh was never declared: sending it would be an unknown-method error the
        // client may report as a server fault.
        Assert.DoesNotContain("workspace/inlayHint/refresh", session.Client.Seen);
    }

    [Fact]
    public async Task ProgressFallsBackToNoOpWhenNothingCanRenderIt()
    {
        var previous = ProgressReporter.Factory;
        ProgressReporter.Factory = (_, _) => throw new InvalidOperationException("client exploded");
        try
        {
            // Progress must never break the work it describes.
            await using var scope = await ProgressReporter.BeginAsync("Loading");
            scope.Report("still fine");
        }
        finally
        {
            ProgressReporter.Factory = previous;
        }
    }

    /// <summary>
    /// Work that finishes quickly must never have announced itself.
    /// </summary>
    /// <remarks>
    /// The <c>workspace/diagnostic</c> sweep is re-requested after anything that could reach
    /// another file and normally answers "unchanged" in milliseconds. Announcing each one put a
    /// "Analyzing solution" notification on screen every time a file was opened, which is what the
    /// user reads as the whole solution reloading.
    /// </remarks>
    [Fact]
    public async Task DeferredProgressStaysSilentForWorkThatFinishesFirst()
    {
        var (recorded, restore) = InstallRecordingFactory();
        try
        {
            await using (var scope = ProgressReporter.BeginDeferred("Analyzing solution", TimeSpan.FromSeconds(30)))
                scope.Report("ProjectA", 50);

            Assert.Empty(recorded);
        }
        finally
        {
            restore();
        }
    }

    [Fact]
    public async Task DeferredProgressAppearsOnceTheWorkOutlastsTheDelay()
    {
        var (recorded, restore) = InstallRecordingFactory();
        try
        {
            await using (var scope = ProgressReporter.BeginDeferred(
                "Analyzing solution", TimeSpan.FromMilliseconds(50)))
            {
                scope.Report("ProjectA", 50);
                await Task.Delay(TimeSpan.FromMilliseconds(750));
            }

            // The title, and the last thing the work said before the scope became visible — a
            // notification that appeared blank would be worse than none.
            Assert.Contains("Analyzing solution", recorded);
            Assert.Contains("ProjectA", recorded);
        }
        finally
        {
            restore();
        }
    }

    /// <summary>
    /// A burst of refresh requests reaches the client as one refresh.
    /// </summary>
    /// <remarks>
    /// A refresh is not a per-document message — it tells the editor to re-pull everything,
    /// including a full <c>workspace/diagnostic</c> sweep. The background analyzer pass fires one
    /// per document, so opening a folder of ten files used to buy ten whole-workspace re-pulls.
    /// </remarks>
    [Fact]
    public async Task ABurstOfRefreshRequestsReachesTheClientOnce()
    {
        await using var session = await EditorSession.StartAsync(new
        {
            processId = 1234,
            rootUri = (string?)null,
            capabilities = new { textDocument = new { diagnostic = new { } } },
        });

        const int requests = 10;
        for (int i = 0; i < requests; i++)
            LspSessionRegistry.ScheduleRefresh(RefreshKind.Diagnostics);

        await session.Client.WaitForAsync("workspace/diagnostic/refresh");

        // Past the coalescing window, so a straggler would have landed by now.
        await Task.Delay(TimeSpan.FromSeconds(2));

        int seen = session.Client.Seen.Count(m => m == "workspace/diagnostic/refresh");

        // Bounded rather than exact: the registry is process-wide and a background analyzer pass
        // left over from another case schedules its own refresh into this session. What is being
        // asserted is that a burst does not cost one refresh per request.
        Assert.InRange(seen, 1, requests - 1);
    }

    private static (ConcurrentBag<string> Recorded, Action Restore) InstallRecordingFactory()
    {
        var previous = ProgressReporter.Factory;
        var recorded = new ConcurrentBag<string>();
        ProgressReporter.Factory = (title, _) =>
        {
            recorded.Add(title);
            return Task.FromResult<IProgressScope>(new RecordingScope(recorded));
        };
        return (recorded, () => ProgressReporter.Factory = previous);
    }

    private sealed class RecordingScope(ConcurrentBag<string> recorded) : IProgressScope
    {
        public void Report(string message, int? percentage = null) => recorded.Add(message);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>An LSP session over an in-memory duplex pair, with the client side recording
    /// every server-initiated request and notification.</summary>
    private sealed class EditorSession : IAsyncDisposable
    {
        private readonly JsonRpc _rpc;
        private readonly Task _serverTask;
        private readonly IAsyncDisposable _services;

        public RecordingClient Client { get; }

        private EditorSession(JsonRpc rpc, RecordingClient client, Task serverTask, IAsyncDisposable services)
        {
            _rpc = rpc;
            Client = client;
            _serverTask = serverTask;
            _services = services;
        }

        public static async Task<EditorSession> StartAsync(object initializeParams)
        {
            var (clientStream, serverStream) = FullDuplexStream.CreatePair();

            var settings = EffectiveSettings.Resolve([], null, out _);
            var services = ToolHostServices.Build(
                settings, new MarkdownFormatter(), FixturePaths.SampleProjectDir);

            var serverTask = LspSessionHost.RunAsync(serverStream, services, CancellationToken.None);

            var client = new RecordingClient();
            var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(
                clientStream, clientStream, new SystemTextJsonFormatter()));
            rpc.AddLocalRpcTarget(client);
            rpc.StartListening();

            await rpc.InvokeWithParameterObjectAsync<InitializeResult>("initialize", initializeParams)
                .WaitAsync(TimeSpan.FromSeconds(30));
            await rpc.NotifyAsync("initialized");

            return new EditorSession(rpc, client, serverTask, services);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _rpc.NotifyAsync("shutdown");
                await _rpc.NotifyAsync("exit");
                await _serverTask.WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch { /* the session is going away either way */ }
            _rpc.Dispose();
            await _services.DisposeAsync();
        }
    }

    /// <summary>The one operation this token names — the same shape for the create request and
    /// for every <c>$/progress</c> that follows it.</summary>
    private static Func<JsonElement, bool> Carrying(string token) =>
        p => p.GetProperty("token").GetString() == token;

    private static string? Title(JsonElement progress) =>
        progress.GetProperty("value").GetProperty("title").GetString();

    private sealed class RecordingClient
    {
        private readonly ConcurrentQueue<(string Key, JsonElement Payload)> _events = new();

        public IReadOnlyCollection<string> Seen => _events.Select(e => e.Key).ToList();

        [JsonRpcMethod("window/workDoneProgress/create", UseSingleObjectParameterDeserialization = true)]
        public object? ProgressCreate(JsonElement p)
        {
            _events.Enqueue(("create", p));
            return null;
        }

        [JsonRpcMethod("$/progress", UseSingleObjectParameterDeserialization = true)]
        public void Progress(JsonElement p) =>
            _events.Enqueue((p.GetProperty("value").GetProperty("kind").GetString()!, p));

        [JsonRpcMethod("workspace/diagnostic/refresh")]
        public object? DiagnosticRefresh()
        {
            _events.Enqueue(("workspace/diagnostic/refresh", default));
            return null;
        }

        [JsonRpcMethod("workspace/codeLens/refresh")]
        public object? CodeLensRefresh()
        {
            _events.Enqueue(("workspace/codeLens/refresh", default));
            return null;
        }

        [JsonRpcMethod("workspace/inlayHint/refresh")]
        public object? InlayHintRefresh()
        {
            _events.Enqueue(("workspace/inlayHint/refresh", default));
            return null;
        }

        public Task<JsonElement> WaitForAsync(string key, int timeoutMs = 10_000) =>
            WaitForAsync(key, static _ => true, timeoutMs);

        /// <summary>The first recorded <paramref name="key"/> event <paramref name="matches"/>
        /// accepts.</summary>
        /// <remarks>
        /// The predicate is how a test says <em>which</em> event it means. A session's stream is
        /// not its own: progress is broadcast to every attached session, so an audit or a restore
        /// another test opened is recorded here too, and asking for the first event of a kind gets
        /// whichever operation the scheduler started first.
        /// </remarks>
        public async Task<JsonElement> WaitForAsync(
            string key, Func<JsonElement, bool> matches, int timeoutMs = 10_000)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                foreach (var (recorded, payload) in _events)
                {
                    if (recorded == key && matches(payload))
                        return payload;
                }

                await Task.Delay(25);
            }
            throw new TimeoutException($"No '{key}' arrived. Seen: {string.Join(", ", Seen)}");
        }
    }
}

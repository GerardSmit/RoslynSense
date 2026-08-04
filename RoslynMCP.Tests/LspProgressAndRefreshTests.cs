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

        await using (var scope = await ProgressReporter.BeginAsync("Loading Sandbox"))
            scope.Report("Restoring packages", 40);

        var created = await session.Client.WaitForAsync("create");
        Assert.StartsWith("roslyn-sense/", created.GetProperty("token").GetString());

        var begin = await session.Client.WaitForAsync("begin");
        Assert.Equal("Loading Sandbox", begin.GetProperty("value").GetProperty("title").GetString());

        var report = await session.Client.WaitForAsync("report");
        Assert.Equal("Restoring packages", report.GetProperty("value").GetProperty("message").GetString());
        Assert.Equal(40, report.GetProperty("value").GetProperty("percentage").GetInt32());

        await session.Client.WaitForAsync("end");
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

        public async Task<JsonElement> WaitForAsync(string key, int timeoutMs = 10_000)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                var match = _events.FirstOrDefault(e => e.Key == key);
                if (match.Key is not null)
                    return match.Payload;
                await Task.Delay(25);
            }
            throw new TimeoutException($"No '{key}' arrived. Seen: {string.Join(", ", Seen)}");
        }
    }
}

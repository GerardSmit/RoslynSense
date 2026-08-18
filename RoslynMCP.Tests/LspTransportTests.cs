using Nerdbank.Streams;
using RoslynMCP.Config;
using RoslynMCP.Daemon;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using StreamJsonRpc;
using Xunit;

namespace RoslynMCP.Tests;

public class LspTransportTests
{
    [Fact]
    public async Task InitializeRoundTripsOverDuplexStream()
    {
        // Same shape as the daemon handoff: LspSessionHost drives one side of a duplex
        // stream, a JSON-RPC client (the "editor") the other.
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();

        var settings = EffectiveSettings.Resolve(Array.Empty<string>(), null, out _);
        await using var services = ToolHostServices.Build(
            settings, new MarkdownFormatter(), FixturePaths.SampleProjectDir);

        var serverTask = LspSessionHost.RunAsync(serverStream, services, CancellationToken.None);

        using var clientRpc = new JsonRpc(new HeaderDelimitedMessageHandler(
            clientStream, clientStream, new SystemTextJsonFormatter()));
        clientRpc.StartListening();

        var result = await clientRpc.InvokeWithParameterObjectAsync<InitializeResult>(
            "initialize",
            new { processId = 1234, rootUri = (string?)null, capabilities = new { } })
            .WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal("RoslynSense", result.ServerInfo.Name);
        Assert.Equal("utf-16", result.Capabilities.PositionEncoding);
        Assert.True(result.Capabilities.DefinitionProvider);
        Assert.True(result.Capabilities.ReferencesProvider);
        Assert.NotNull(result.Capabilities.TextDocumentSync);
        Assert.Equal(2, result.Capabilities.TextDocumentSync!.Change); // incremental

        await clientRpc.NotifyAsync("shutdown");
        await clientRpc.NotifyAsync("exit");
        await serverTask.WaitAsync(TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// The editor announcing an app it launched, and reading the registry back: the two halves
    /// that let a chat see what the user is running, over the real RPC rather than by calling
    /// the registry directly.
    /// </summary>
    [Fact]
    public async Task EditorLaunchedProcessRoundTripsThroughTheRegistry()
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();

        var settings = EffectiveSettings.Resolve(Array.Empty<string>(), null, out _);
        await using var services = ToolHostServices.Build(
            settings, new MarkdownFormatter(), FixturePaths.SampleProjectDir);

        var serverTask = LspSessionHost.RunAsync(serverStream, services, CancellationToken.None);

        using var clientRpc = new JsonRpc(new HeaderDelimitedMessageHandler(
            clientStream, clientStream, new SystemTextJsonFormatter()));
        clientRpc.StartListening();

        await clientRpc.InvokeWithParameterObjectAsync<InitializeResult>(
            "initialize",
            new { processId = 1234, rootUri = (string?)null, capabilities = new { } })
            .WaitAsync(TimeSpan.FromSeconds(15));

        // This test process stands in for the launched app: a PID that is certainly alive, so
        // List() does not prune the entry on the way back out.
        int pid = Environment.ProcessId;
        try
        {
            await clientRpc.InvokeWithParameterObjectAsync<string>(
                "roslynSense/registerProcess",
                new { pid, projectPath = FixturePaths.SampleProjectDir + "/Sample.csproj", url = "http://localhost:5123" })
                .WaitAsync(TimeSpan.FromSeconds(15));

            var running = await clientRpc.InvokeAsync<RunningProcess[]>("roslynSense/runningProcesses")
                .WaitAsync(TimeSpan.FromSeconds(15));

            var entry = Assert.Single(running, p => p.Pid == pid);
            Assert.Equal("http://localhost:5123", entry.Url);
            Assert.StartsWith("editor-", entry.SessionId);
        }
        finally
        {
            await clientRpc.InvokeWithParameterObjectAsync<object>(
                "roslynSense/unregisterProcess", new { pid })
                .WaitAsync(TimeSpan.FromSeconds(15));
        }

        var after = await clientRpc.InvokeAsync<RunningProcess[]>("roslynSense/runningProcesses")
            .WaitAsync(TimeSpan.FromSeconds(15));
        Assert.DoesNotContain(after, p => p.Pid == pid);

        await clientRpc.NotifyAsync("shutdown");
        await clientRpc.NotifyAsync("exit");
        await serverTask.WaitAsync(TimeSpan.FromSeconds(15));
    }
}

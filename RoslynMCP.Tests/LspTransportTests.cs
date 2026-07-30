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
}

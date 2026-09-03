using System.Text.Json.Serialization;
using Nerdbank.Streams;
using RoslynMCP.Config;
using RoslynMCP.Daemon;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Services.Packages;
using StreamJsonRpc;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The package panel's LSP surface, driven over the same duplex stream the daemon hands the editor.
/// </summary>
public class NuGetLspTests
{
    [Fact]
    public async Task SourcesAndIconRoundTripOverTheDuplexStream()
    {
        await using var session = await LspSession.StartAsync();

        var sources = await session.Rpc
            .InvokeWithParameterObjectAsync<PackageSourceDescription[]>("roslynSense/nuget/sources", new { })
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(sources);

        // A package with no icon answers with a null data URI rather than an error: a missing icon
        // is a fallback glyph, not a failure.
        var icon = await session.Rpc
            .InvokeWithParameterObjectAsync<IconResult>(
                "roslynSense/nuget/icon",
                new { id = $"Nonexistent.Package.{Guid.NewGuid():N}", allowDownload = false })
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(icon.DataUri);

        await session.ShutdownAsync();
    }

    [Fact]
    public async Task MetadataForAnUnknownPackageIsNullRatherThanAnError()
    {
        await using var session = await LspSession.StartAsync();

        var metadata = await session.Rpc
            .InvokeWithParameterObjectAsync<object?>(
                "roslynSense/nuget/metadata",
                new { id = $"Nonexistent.Package.{Guid.NewGuid():N}", version = "1.0.0", includeReadme = false })
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(metadata);

        await session.ShutdownAsync();
    }

    [Fact]
    public async Task UpdateAllWithNothingSelectedReportsRatherThanThrows()
    {
        await using var session = await LspSession.StartAsync();

        var result = await session.Rpc
            .InvokeWithParameterObjectAsync<UpdateAllResult>(
                "roslynSense/nuget/updateAll",
                new { packages = Array.Empty<object>(), restore = false })
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(result.Success);
        Assert.Empty(result.Results);

        await session.ShutdownAsync();
    }

    /// <summary>
    /// The regression test for the MCP-stdio hang: with no client handler registered, the
    /// credential prompt must answer immediately rather than blocking a package operation until
    /// something times out.
    /// </summary>
    [Fact]
    public async Task CredentialRequestReturnsNullWhenNoClientHandlesIt()
    {
        await using var session = await LspSession.StartAsync();

        var reply = await NuGetCredentialPrompt.Handler!(
            new NuGetCredentialRequest("https://example.invalid", "Example", null, false),
            CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(reply);

        await session.ShutdownAsync();
    }

    // The server names every wire member explicitly, so these do too rather than relying on
    // whatever casing convention the formatter happens to default to.
    private sealed record PackageSourceDescription(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("isEnabled")] bool IsEnabled);

    private sealed record IconResult(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("dataUri")] string? DataUri);

    private sealed record UpdateAllResult(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("results")] object[] Results);

    /// <summary>An in-process LSP session, the cheap alternative to spawning the real executable.</summary>
    private sealed class LspSession : IAsyncDisposable
    {
        private readonly IAsyncDisposable _services;
        private readonly Task _serverTask;

        public JsonRpc Rpc { get; }

        private LspSession(JsonRpc rpc, IAsyncDisposable services, Task serverTask)
        {
            Rpc = rpc;
            _services = services;
            _serverTask = serverTask;
        }

        public static async Task<LspSession> StartAsync()
        {
            var (clientStream, serverStream) = FullDuplexStream.CreatePair();

            var settings = EffectiveSettings.Resolve([], null, out _);
            var services = ToolHostServices.Build(
                settings, new MarkdownFormatter(), FixturePaths.SampleProjectDir);

            var serverTask = LspSessionHost.RunAsync(serverStream, services, CancellationToken.None);

            var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(
                clientStream, clientStream, new SystemTextJsonFormatter()));
            rpc.StartListening();

            await rpc.InvokeWithParameterObjectAsync<InitializeResult>(
                "initialize",
                new { processId = 1234, rootUri = (string?)null, capabilities = new { } })
                .WaitAsync(TimeSpan.FromSeconds(30));

            return new LspSession(rpc, services, serverTask);
        }

        public async Task ShutdownAsync()
        {
            await Rpc.NotifyAsync("shutdown");
            await Rpc.NotifyAsync("exit");
            await _serverTask.WaitAsync(TimeSpan.FromSeconds(15));
        }

        public async ValueTask DisposeAsync()
        {
            Rpc.Dispose();
            await _services.DisposeAsync();
        }
    }
}

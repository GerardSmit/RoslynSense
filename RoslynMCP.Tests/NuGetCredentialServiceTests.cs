using System.Diagnostics;
using System.Net;
using NuGet.Configuration;
using RoslynMCP.Services.Packages;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Feed authentication.
/// </summary>
/// <remarks>
/// The failure mode being guarded is a hang, not an exception. A credential provider that waits
/// for an answer nobody will give blocks the package operation that triggered it, and in an
/// MCP-only process there is no editor to answer at all.
/// </remarks>
[Collection(SharedState.Name)]
public class NuGetCredentialServiceTests
{
    [Fact]
    public void InstallIsIdempotent()
    {
        // Reached from every feed query, so the cost after the first call has to be nothing.
        NuGetCredentialService.Install();
        NuGetCredentialService.Install();
        NuGetCredentialService.Install();
    }

    [Fact]
    public void InstallResolvesTheDotnetHostForPluginProviders()
    {
        NuGetCredentialService.Install();

        // Plugin credential providers launch through the dotnet host and NuGet silently skips
        // them when this is unset — which presents as an unexplained 401 on an Azure Artifacts
        // feed. The test suite always runs under a dotnet host, so it must be resolvable here.
        string? host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");

        Assert.False(string.IsNullOrWhiteSpace(host));
        Assert.True(File.Exists(host), $"DOTNET_HOST_PATH points at {host}, which is not there.");
    }

    [Fact]
    public async Task WithNoPromptInstalledTheRequestReturnsImmediately()
    {
        using var _ = new PromptSwap(null);
        var service = new NuGetCredentialService.EditorPromptCredentialService(new NoCredentials());

        var stopwatch = Stopwatch.StartNew();
        var credentials = await Ask(service);
        stopwatch.Stop();

        // No editor attached — the MCP-only case. The answer is "nobody can be asked", delivered
        // now rather than after a timeout the caller has to sit through.
        Assert.Null(credentials);
        Assert.True(stopwatch.ElapsedMilliseconds < 2000, $"took {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task ConcurrentRequestsForOneFeedPromptOnce()
    {
        int prompts = 0;
        using var _ = new PromptSwap(async (_, _) =>
        {
            Interlocked.Increment(ref prompts);
            await Task.Delay(50);
            return new NuGetCredentialReply("user", "token");
        });

        var service = new NuGetCredentialService.EditorPromptCredentialService(new NoCredentials());

        // Eight parallel version lookups against one feed that answers 401. Without the semaphore
        // and the per-origin cache the user gets eight password boxes.
        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => Ask(service)));

        Assert.All(results, credentials => Assert.NotNull(credentials));
        Assert.Equal(1, prompts);
    }

    [Fact]
    public async Task ADismissedPromptAnswersNullRatherThanThrowing()
    {
        using var _ = new PromptSwap((_, _) => Task.FromResult<NuGetCredentialReply?>(null));
        var service = new NuGetCredentialService.EditorPromptCredentialService(new NoCredentials());

        // Dismissing has to leave the feed reported as unauthenticated, which is recoverable,
        // rather than failing the whole search.
        Assert.Null(await Ask(service, $"https://dismissed-{Guid.NewGuid():N}.invalid"));
    }

    [Fact]
    public async Task AProxyChallengeIsNotThePackagePanelsBusiness()
    {
        bool prompted = false;
        using var _ = new PromptSwap((_, _) =>
        {
            prompted = true;
            return Task.FromResult<NuGetCredentialReply?>(new NuGetCredentialReply("u", "p"));
        });

        var service = new NuGetCredentialService.EditorPromptCredentialService(new NoCredentials());

        await service.GetCredentialsAsync(
            new Uri($"https://proxy-{Guid.NewGuid():N}.invalid"), proxy: null,
            CredentialRequestType.Proxy, "proxy", CancellationToken.None);

        Assert.False(prompted);
    }

    private static Task<ICredentials?> Ask(
        NuGetCredentialService.EditorPromptCredentialService service, string? uri = null) =>
        service.GetCredentialsAsync(
            new Uri(uri ?? "https://feed.invalid"),
            proxy: null,
            CredentialRequestType.Unauthorized,
            "401",
            CancellationToken.None);

    /// <summary>NuGet's own providers, having come up empty — the only case that reaches the editor.</summary>
    private sealed class NoCredentials : ICredentialService
    {
        public bool HandlesDefaultCredentials => false;

        public Task<ICredentials?> GetCredentialsAsync(
            Uri uri, IWebProxy? proxy, CredentialRequestType type, string message, CancellationToken ct) =>
            Task.FromResult<ICredentials?>(null);

        public bool TryGetLastKnownGoodCredentialsFromCache(Uri uri, bool isProxy, out ICredentials credentials)
        {
            credentials = null!;
            return false;
        }
    }

    private sealed class PromptSwap : IDisposable
    {
        private readonly Func<NuGetCredentialRequest, CancellationToken, Task<NuGetCredentialReply?>>? _previous;

        public PromptSwap(Func<NuGetCredentialRequest, CancellationToken, Task<NuGetCredentialReply?>>? handler)
        {
            _previous = NuGetCredentialPrompt.Handler;
            NuGetCredentialPrompt.Handler = handler;
        }

        public void Dispose() => NuGetCredentialPrompt.Handler = _previous;
    }
}

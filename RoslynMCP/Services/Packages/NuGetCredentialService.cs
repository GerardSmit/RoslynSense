using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Credentials;
using NuGet.Protocol;

namespace RoslynMCP.Services.Packages;

/// <summary>What the editor is asked when a feed rejects the request.</summary>
public sealed record NuGetCredentialRequest(string Uri, string? SourceName, string? Message, bool IsRetry);

/// <summary>What the user typed. Never persisted on this side.</summary>
public sealed record NuGetCredentialReply(string Username, string Password);

/// <summary>
/// The editor's half of feed authentication, installed by the LSP layer.
/// </summary>
/// <remarks>
/// A delegate rather than a direct call into <c>LspSessionRegistry</c>, matching
/// <see cref="ProgressReporter.Factory"/> and <see cref="ServiceLog.Sink"/>: services must keep
/// working in an MCP-only process where no editor is attached at all.
/// </remarks>
public static class NuGetCredentialPrompt
{
    public static Func<NuGetCredentialRequest, CancellationToken, Task<NuGetCredentialReply?>>? Handler { get; set; }
}

/// <summary>
/// Wires NuGet's own credential chain into the daemon, so a private feed behaves the way it does
/// on the command line.
/// </summary>
/// <remarks>
/// The chain is NuGet.config credentials, then the plugin credential providers Azure Artifacts
/// and friends install, then — only if both come up empty — a prompt in the editor. Rebuilding
/// that chain by hand would mean naming <c>SecurePluginCredentialProviderBuilder</c>,
/// <c>PluginManager</c> and friends, whose signatures move between NuGet minor versions; a
/// compile break there would take out all package management. Wrapping the default service means
/// depending only on <see cref="ICredentialService"/>, which is three members and stable.
/// </remarks>
public static class NuGetCredentialService
{
    private static int s_installed;

    /// <summary>
    /// Installs the credential chain. Idempotent, and safe to call from anywhere that is about
    /// to touch a feed.
    /// </summary>
    /// <remarks>
    /// Ordering matters more than it looks: NuGet reads
    /// <see cref="HttpHandlerResourceV3.CredentialService"/> once, when it builds the HTTP handler
    /// for a source. Installing after the first <c>SourceRepository</c> exists means that feed
    /// authenticates as nobody for the rest of the process.
    /// </remarks>
    public static void Install()
    {
        if (Interlocked.Exchange(ref s_installed, 1) != 0)
            return;

        try
        {
            EnsureDotnetHostPath();
            InstallCore();
        }
        catch (Exception ex)
        {
            // Package management still works against public feeds without this.
            ServiceLog.Warn($"Could not install the NuGet credential provider: {ex.Message}", key: "nuget-cred-install");
        }
    }

    // NoInlining: NuGet.Credentials types must not be resolved unless Install() actually runs.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InstallCore()
    {
        // nonInteractive suppresses NuGet's own console prompt. In a daemon whose stdout is a
        // JSON-RPC channel there is nobody to answer a Console.ReadLine, so it would hang
        // forever; the editor prompt below supplies the interactivity instead.
        DefaultCredentialServiceUtility.SetupDefaultCredentialService(new CredentialLogger(), nonInteractive: true);

        var inner = HttpHandlerResourceV3.CredentialService;
        HttpHandlerResourceV3.CredentialService =
            new Lazy<ICredentialService>(() => new EditorPromptCredentialService(inner.Value));
    }

    /// <summary>
    /// Plugin credential providers launch through the dotnet host, and NuGet silently skips them
    /// when <c>DOTNET_HOST_PATH</c> is unset — which presents as an unexplained 401 on an Azure
    /// Artifacts feed. MSBuildLocator has already found an SDK by this point, so the host is
    /// usually two directories above its MSBuild path.
    /// </summary>
    private static void EnsureDotnetHostPath()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 })
            return;

        if (FindDotnetHost() is { } host)
        {
            Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", host);
            return;
        }

        ServiceLog.Warn(
            "DOTNET_HOST_PATH is not set, so NuGet credential providers (Azure Artifacts and " +
            "similar) will be skipped and private feeds may fail to authenticate.",
            key: "nuget-dotnet-host-path");
    }

    private static string? FindDotnetHost()
    {
        string exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

        // An SDK-based MSBuild lives at <dotnet-root>/sdk/<version>, so the host is two up.
        if (MsBuildRootPath() is { Length: > 0 } msbuild)
        {
            string? sdkDirectory = Path.GetDirectoryName(msbuild.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string? dotnetRoot = Path.GetDirectoryName(sdkDirectory);
            if (dotnetRoot is { Length: > 0 } && File.Exists(Path.Combine(dotnetRoot, exe)))
                return Path.Combine(dotnetRoot, exe);
        }

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(directory, exe);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // A malformed PATH entry is not worth failing over.
            }
        }

        return null;
    }

    // NoInlining and separate from the caller: Microsoft.Build.Locator resolves only after
    // WorkspaceService's static constructor has run, and this is reached from feed code that
    // may run before it.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? MsBuildRootPath()
    {
        try
        {
            WorkspaceService.EnsureRegistered();
            return Microsoft.Build.Locator.MSBuildLocator.IsRegistered
                ? Microsoft.Build.Locator.MSBuildLocator.QueryVisualStudioInstances().FirstOrDefault()?.MSBuildPath
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Falls back to asking the editor when NuGet's own providers cannot produce a credential.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so the two behaviours worth pinning down — never blocking when
    /// nobody can answer, and never opening one prompt per concurrent request — can be tested
    /// without standing up a real feed.
    /// </remarks>
    internal sealed class EditorPromptCredentialService(ICredentialService inner) : ICredentialService
    {
        private static readonly SemaphoreSlim s_prompt = new(1, 1);
        private static readonly ConcurrentDictionary<string, ICredentials> s_byOrigin = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Origins whose cached credential NuGet already tried. A second ask means it was rejected.</summary>
        private static readonly ConcurrentDictionary<string, bool> s_handedOut = new(StringComparer.OrdinalIgnoreCase);

        public bool HandlesDefaultCredentials => inner.HandlesDefaultCredentials;

        public bool TryGetLastKnownGoodCredentialsFromCache(
            Uri uri, bool isProxy, out ICredentials credentials)
        {
            if (!isProxy && s_byOrigin.TryGetValue(Origin(uri), out var cached))
            {
                credentials = cached;
                return true;
            }
            return inner.TryGetLastKnownGoodCredentialsFromCache(uri, isProxy, out credentials);
        }

        public async Task<ICredentials?> GetCredentialsAsync(
            Uri uri, IWebProxy? proxy, CredentialRequestType type, string message, CancellationToken ct)
        {
            var provided = await inner.GetCredentialsAsync(uri, proxy, type, message, ct);
            if (provided is not null)
                return provided;

            // A proxy credential is the operating system's business, not the package panel's.
            if (type == CredentialRequestType.Proxy || NuGetCredentialPrompt.Handler is not { } handler)
                return null;

            string origin = Origin(uri);

            // What this caller could see before queueing. A credential that appears while we wait
            // was answered by someone ahead of us in the same wave, and telling that apart from a
            // rejection is what the snapshot is for — see below.
            s_byOrigin.TryGetValue(origin, out var beforeWait);

            // Eight parallel version lookups against one 401'ing feed would otherwise open eight
            // password boxes. The first prompt wins and the rest reuse its answer.
            await s_prompt.WaitAsync(ct);
            try
            {
                // The credential changed while we were queued, so the caller ahead of us just
                // prompted and this is the answer we were waiting for. Checked before the retry
                // test below, because that test cannot tell a concurrent peer from a rejection:
                // it reads a single per-origin flag that the first peer through the semaphore
                // consumes, so every remaining peer would call itself a retry, wipe the fresh
                // credential and prompt again — one password box each, which is the exact thing
                // the semaphore is here to prevent.
                if (s_byOrigin.TryGetValue(origin, out var fresh) && !ReferenceEquals(fresh, beforeWait))
                {
                    s_handedOut[origin] = true;
                    return fresh;
                }

                // NuGet only asks again after the credential it was handed was rejected. Serving
                // the cached one a second time would replay a wrong password forever: NuGet gives
                // up when the answer does not change, so the feed would 401 for the rest of the
                // process and the user would never be asked to correct it.
                bool retry = s_handedOut.TryRemove(origin, out _);
                if (retry)
                    s_byOrigin.TryRemove(origin, out _);
                else if (s_byOrigin.TryGetValue(origin, out var cached))
                {
                    s_handedOut[origin] = true;
                    return cached;
                }

                // Two minutes, not seconds: a user pasting a personal access token is slow, and a
                // client that will never answer must still unblock the request eventually.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));

                var reply = await handler(
                    new NuGetCredentialRequest(origin, SourceNameFor(origin), message, retry),
                    timeout.Token);

                if (reply is null)
                    return null;

                var credentials = new NetworkCredential(reply.Username, reply.Password);
                s_byOrigin[origin] = credentials;
                s_handedOut[origin] = true;
                return credentials;
            }
            catch (OperationCanceledException)
            {
                // The user dismissed the prompt, or nobody answered. The feed is reported as
                // failed, which is recoverable, rather than the whole search throwing.
                return null;
            }
            finally
            {
                s_prompt.Release();
            }
        }

        /// <summary>
        /// Scheme, host and port only. The failing request URL carries the package id and query,
        /// which the editor has no business displaying in a sign-in prompt.
        /// </summary>
        private static string Origin(Uri uri) => uri.GetLeftPart(UriPartial.Authority);

        private static string? SourceNameFor(string origin)
        {
            try
            {
                return NuGetFeedContext.Sources()
                    .FirstOrDefault(s => s.Source.StartsWith(origin, StringComparison.OrdinalIgnoreCase))
                    ?.Name;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>Credential-provider diagnostics, which are otherwise invisible when auth fails.</summary>
    private sealed class CredentialLogger : LoggerBase
    {
        public override void Log(ILogMessage message)
        {
            if (message.Level >= LogLevel.Warning)
                ServiceLog.Warn($"NuGet credentials: {message.Message}", key: "nuget-cred-log");
        }

        public override Task LogAsync(ILogMessage message)
        {
            Log(message);
            return Task.CompletedTask;
        }
    }
}

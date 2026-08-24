using System.Collections.Concurrent;
using System.Collections.Immutable;
using NuGet.Versioning;
using RoslynMCP.Lsp;
using RoslynMCP.Services.Packages;

namespace RoslynMCP.Languages.MsBuild.Core;

/// <summary>What a feed says about one exact package version.</summary>
/// <param name="Versions">Every version the feeds offered, for deciding what "outdated" means.</param>
/// <param name="Exists">Whether the requested version was among them.</param>
/// <param name="FeedsHealthy">Whether every feed actually answered. The difference between "no such
/// version" and "nobody could tell us" — see <see cref="PackageStatusCache"/>.</param>
internal sealed record PackageStatus(
    ImmutableArray<NuGetVersion> Versions,
    bool Exists,
    ImmutableArray<PackageVulnerabilityInfo> Vulnerabilities,
    PackageDeprecationInfo? Deprecation,
    bool FeedsHealthy,
    DateTime FetchedUtc);

/// <summary>
/// Package facts for the diagnostics path, which may never wait for them.
/// </summary>
/// <remarks>
/// <para>
/// The invariant this type exists to hold: <c>DiagnosticsAsync</c> never awaits a network call. It
/// runs on the debounced publish path and on <c>textDocument/diagnostic</c>, so a feed timeout there
/// is a stall on every keystroke in a project file. So the read is synchronous and warm-only, and a
/// miss reports nothing while a fetch is started behind it.
/// </para>
/// <para>
/// This is the shape <c>AnalyzerDiagnosticCache</c> already uses for the same problem in C#: read
/// what is cached, start the expensive half detached, and ask the client to re-pull when it lands.
/// The pattern is copied rather than invented, down to the detail that makes it work — the fetch
/// runs under <see cref="CancellationToken.None"/> rather than the request's token, because the
/// request is cancelled by the very next keystroke and a fetch that died with it would mean a
/// steadily-typing user never gets an answer at all.
/// </para>
/// </remarks>
internal static class PackageStatusCache
{
    /// <summary>
    /// Matches <c>NuGetMetadataService</c>'s own, and for its reason: deprecation and vulnerability
    /// are the two facts that change under a version that does not.
    /// </summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How many feed lookups run at once.
    /// </summary>
    /// <remarks>
    /// A <c>Directory.Packages.props</c> in a monorepo carries several hundred entries, and opening
    /// it primes every one of them. Unbounded, that is several hundred simultaneous sockets on a
    /// file the user merely looked at.
    /// </remarks>
    private static readonly SemaphoreSlim s_gate = new(4, 4);

    private static readonly ConcurrentDictionary<string, PackageStatus> s_ready =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, byte> s_inFlight =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Counts fetches, so tests can assert that a warm read started none.</summary>
    internal static long Fetches;

    private static string Key(string id, string version) => $"{id}/{version}";

    /// <summary>
    /// What is known now, or null. Never touches the network.
    /// </summary>
    /// <remarks>
    /// An entry past its lifetime is still served, and a refresh is queued behind it. Blanking the
    /// squiggles while a refetch runs is the failure the C# path already documents — every warning
    /// in the window blinking out and coming back — and stale-but-shown beats correct-but-flickering
    /// for a fact that changes about once a month.
    /// </remarks>
    public static PackageStatus? TryGet(string id, string version)
    {
        if (!s_ready.TryGetValue(Key(id, version), out var status))
            return null;

        if (DateTime.UtcNow - status.FetchedUtc > Lifetime)
            Prime(id, version);

        return status;
    }

    /// <summary>
    /// Starts a fetch and returns immediately.
    /// </summary>
    /// <remarks>
    /// Deduplicated per <c>(id, version)</c>: forty references to the same package across a solution
    /// are one lookup. When it lands it asks the client to re-pull, through the coalescing scheduler
    /// rather than directly — forty packages finishing at once would otherwise be forty
    /// workspace-wide refreshes.
    /// </remarks>
    public static void Prime(string id, string version)
    {
        if (id.Length == 0)
            return;

        string key = Key(id, version);
        if (!s_inFlight.TryAdd(key, 0))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await s_gate.WaitAsync(CancellationToken.None);

                try
                {
                    s_ready[key] = await FetchAsync(id, version);
                }
                finally
                {
                    s_gate.Release();
                }

                LspSessionRegistry.ScheduleRefresh(RefreshKind.Diagnostics, "package-status");
            }
            catch (Exception ex)
            {
                // A failed warm costs the next pull a miss and nothing else. It must not take the
                // process down, and it must not be reported as a fact about the package.
                Console.Error.WriteLine($"[MsBuild] Package status for '{key}' failed: {ex.Message}");
            }
            finally
            {
                s_inFlight.TryRemove(key, out _);
            }
        });
    }

    private static async Task<PackageStatus> FetchAsync(string id, string version)
    {
        Interlocked.Increment(ref Fetches);

        var lookup = await NuGetService.AllVersionsAsync(
            id, includePrerelease: true, refresh: false, CancellationToken.None);

        // Every feed answered, and none of them errored or asked for credentials. Anything less and
        // the absence of a version says nothing about whether it exists.
        bool healthy = lookup.Feeds.Count > 0 && lookup.Feeds.All(f => f.Ok);

        var versions = lookup.Results.ToImmutableArray();
        bool exists = NuGetVersion.TryParse(version, out var parsed)
            && versions.Any(v => v.Equals(parsed));

        // Only for a version that is actually out there. Asking the registration index about a
        // version nobody published is a round trip whose answer is always null.
        PackageMetadataDetail? detail = exists
            ? await NuGetMetadataService.GetAsync(
                id, version, includePrerelease: true, includeReadme: false, refresh: false,
                CancellationToken.None)
            : null;

        return new PackageStatus(
            versions,
            exists,
            detail?.Vulnerabilities?.ToImmutableArray() ?? [],
            detail?.Deprecation,
            healthy,
            DateTime.UtcNow);
    }

    /// <summary>
    /// Drops everything.
    /// </summary>
    /// <remarks>
    /// Whole rather than per package, because the events that call it change what every feed would
    /// answer: a source added or removed, a credential fixed, a <c>NuGet.config</c> edited. A
    /// mutation to one package is different and invalidates only that one.
    /// </remarks>
    public static void Invalidate()
    {
        s_ready.Clear();
        s_inFlight.Clear();
    }

    /// <summary>Drops one package, for when one package is what changed.</summary>
    public static void Invalidate(string id)
    {
        foreach (string key in s_ready.Keys)
        {
            if (key.StartsWith(id + "/", StringComparison.OrdinalIgnoreCase))
                s_ready.TryRemove(key, out _);
        }
    }

    internal static void Clear()
    {
        Invalidate();
        Interlocked.Exchange(ref Fetches, 0);
    }

    /// <summary>Seeds an entry, so tests can exercise the reporting without a feed.</summary>
    internal static void Seed(string id, string version, PackageStatus status) =>
        s_ready[Key(id, version)] = status;
}

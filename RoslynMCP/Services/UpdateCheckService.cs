using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace RoslynMCP.Services;

/// <summary>
/// Reports when a newer RoslynSense has been published, without costing anything at startup.
/// </summary>
/// <remarks>
/// <para>
/// A <c>dotnet tool update</c> takes roughly five seconds even when there is nothing to do, so
/// running one per session is far too expensive to be worth it. This instead asks NuGet for the
/// version list — one small request — on a background task, caches the answer for a day, and never
/// blocks anything. A session that finds a fresh cache makes no network call at all.
/// </para>
/// <para>
/// It only ever reports; updating is left to the user, because the running server holds its own
/// binary and cannot replace it in place.
/// </para>
/// </remarks>
internal static class UpdateCheckService
{
    private const string PackageId = "RoslynSense";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private static readonly string CachePath = Path.Combine(
        Path.GetTempPath(), "RoslynMCP", "update-check.json");

    private static string? s_latestKnown;

    /// <summary>The running server's version, or <c>null</c> when it cannot be determined.</summary>
    public static string? CurrentVersion { get; } = ReadCurrentVersion();

    /// <summary>Set when <c>ROSLYNMCP_NO_UPDATE_CHECK</c> disables the check.</summary>
    public static bool Disabled { get; } =
        Environment.GetEnvironmentVariable("ROSLYNMCP_NO_UPDATE_CHECK") is "1" or "true" or "on";

    /// <summary>
    /// Starts a check if the cached answer has expired. Returns immediately; the result is picked
    /// up by a later call to <see cref="GetHint"/>, typically in a subsequent session.
    /// </summary>
    public static void BeginCheck()
    {
        if (Disabled || CurrentVersion is null)
            return;

        var cached = ReadCache();
        s_latestKnown = cached.Latest;

        if (cached.CheckedUtc is { } checkedUtc && DateTime.UtcNow - checkedUtc < CacheLifetime)
            return; // Still fresh: no request, no cost.

        _ = Task.Run(RefreshAsync);
    }

    /// <summary>
    /// A one-line notice when a newer version exists, or <c>null</c>. Reads only what is already
    /// known, so it never waits on the network.
    /// </summary>
    public static string? GetHint()
    {
        if (Disabled || CurrentVersion is null || s_latestKnown is null)
            return null;

        if (!IsNewer(s_latestKnown, CurrentVersion))
            return null;

        return $"A newer RoslynSense is available ({CurrentVersion} → {s_latestKnown}). " +
               "Update with `dotnet tool update --global RoslynSense`, then restart the session. " +
               "An update that fails because the files are in use can be ignored — it applies on " +
               "the next start.";
    }

    private static async Task RefreshAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = RequestTimeout };

            // The flat container index is the cheapest published listing: a bare version array,
            // no auth, no search ranking.
            var url = $"https://api.nuget.org/v3-flatcontainer/{PackageId.ToLowerInvariant()}/index.json";
            using var document = JsonDocument.Parse(await client.GetStringAsync(url));

            if (!document.RootElement.TryGetProperty("versions", out var versions))
                return;

            var latest = versions.EnumerateArray()
                .Select(v => v.GetString())
                .Where(v => !string.IsNullOrEmpty(v) && !v!.Contains('-')) // skip prerelease
                .OrderBy(v => ParseVersion(v!))
                .LastOrDefault();

            if (latest is null)
                return;

            s_latestKnown = latest;
            WriteCache(latest);
        }
        catch (Exception)
        {
            // Offline, blocked, or NuGet unavailable. A missing update notice is not worth
            // reporting; the next session tries again.
        }
    }

    private static (DateTime? CheckedUtc, string? Latest) ReadCache()
    {
        try
        {
            if (!File.Exists(CachePath))
                return (null, null);

            using var document = JsonDocument.Parse(File.ReadAllText(CachePath));
            var root = document.RootElement;

            var checkedUtc = root.TryGetProperty("checkedUtc", out var c) && c.TryGetDateTime(out var parsed)
                ? parsed
                : (DateTime?)null;
            var latest = root.TryGetProperty("latest", out var l) ? l.GetString() : null;

            return (checkedUtc, latest);
        }
        catch
        {
            return (null, null);
        }
    }

    private static void WriteCache(string latest)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(new
            {
                checkedUtc = DateTime.UtcNow,
                latest,
            }));
        }
        catch
        {
            // A cache that cannot be written just means the next session checks again.
        }
    }

    private static string? ReadCurrentVersion()
    {
        var assembly = typeof(UpdateCheckService).Assembly;

        // InformationalVersion carries the NuGet version; AssemblyVersion is padded to four parts.
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrEmpty(informational))
        {
            // Strip any source-control suffix, e.g. "0.1.28+abc123".
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        return FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion;
    }

    internal static bool IsNewer(string candidate, string current) =>
        ParseVersion(candidate) > ParseVersion(current);

    private static Version ParseVersion(string value)
    {
        // Ignore prerelease/build metadata; only the numeric core orders releases.
        var core = value.Split('-', '+')[0];
        return Version.TryParse(core, out var parsed) ? parsed : new Version(0, 0);
    }
}

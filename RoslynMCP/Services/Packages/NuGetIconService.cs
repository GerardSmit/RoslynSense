using System.Collections.Concurrent;

namespace RoslynMCP.Services.Packages;

/// <summary>
/// Package icons, as data URIs the webview can actually render.
/// </summary>
/// <remarks>
/// The panel's content security policy forbids remote images outright, so an icon has to be
/// proxied here or not shown at all. Every icon is resolved once per process and kept on disk
/// afterwards: the panel asks for one icon per visible row, and re-fetching them on every search
/// is what used to make results appear seconds after the list did.
/// </remarks>
public static class NuGetIconService
{
    /// <summary>Real feeds serve 512-pixel PNGs; anything past this is not an icon.</summary>
    private const int MaxBytes = 1024 * 1024;

    /// <summary>How long a package with no icon is believed to still have no icon.</summary>
    private static readonly TimeSpan MissLifetime = TimeSpan.FromHours(24);

    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static readonly ConcurrentDictionary<string, Lazy<Task<string?>>> s_icons =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Hosts that failed and when, so one dead CDN does not stall every row of a list. Entries
    /// expire: a laptop resuming from sleep mid-request should not blank every icon until the
    /// daemon restarts.
    /// </summary>
    private static readonly Dictionary<string, DateTime> s_failedHosts = [];

    private static readonly TimeSpan FailedHostLifetime = TimeSpan.FromMinutes(5);

    private static string IconDirectory => Path.Combine(NuGetPayloadService.CacheDirectory, "icons");

    /// <summary>
    /// The icon for a package, as a data URI.
    /// </summary>
    /// <param name="allowPackageDownload">
    /// Whether the .nupkg may be fetched when the feed exposes no icon URL. False while browsing:
    /// the feeds that omit icon URLs are private ones, and thirty downloads to paint a list the
    /// user is scrolling past is not a trade worth making. True for installed packages, whose
    /// .nupkg is already in the global packages folder.
    /// </param>
    public static Task<string?> ResolveAsync(
        string id, string? version, string? iconUrl, bool allowPackageDownload, CancellationToken ct)
    {
        // allowPackageDownload is part of the key: a Browse-tab miss means "was not allowed to open
        // the package", not "has no icon". Sharing a key with the Installed tab — which is allowed
        // — would let the cheap lookup's null answer suppress the one that would have found it.
        string key = iconUrl is { Length: > 0 }
            ? iconUrl
            : $"embedded:{id}/{version ?? "latest"}/{allowPackageDownload}".ToLowerInvariant();

        var entry = s_icons.GetOrAdd(key, _ => new Lazy<Task<string?>>(
            () => LoadAsync(key, id, version, iconUrl, allowPackageDownload, ct),
            LazyThreadSafetyMode.ExecutionAndPublication));

        var task = entry.Value;

        // Canceled counts as well as faulted. The entry captures the first caller's token, so a
        // user who scrolls past a row mid-fetch would otherwise leave a canceled task cached under
        // that key — and every later caller would get the cancellation, for the life of the daemon.
        if (task.IsCompleted && !task.IsCompletedSuccessfully)
        {
            s_icons.TryRemove(key, out _);
            return Task.FromResult<string?>(null);
        }

        return task;
    }

    /// <summary>
    /// An icon fetched straight from a URL.
    /// </summary>
    /// <remarks>
    /// Kept as its own entry point because the scheme check is the security-relevant part: the URL
    /// comes from package metadata, which is to say from a stranger.
    /// </remarks>
    public static async Task<string?> FromUrlAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            return null;

        lock (s_failedHosts)
        {
            if (s_failedHosts.TryGetValue(uri.Host, out var failedAt))
            {
                if (DateTime.UtcNow - failedAt < FailedHostLifetime)
                    return null;
                s_failedHosts.Remove(uri.Host);
            }
        }

        try
        {
            using var response = await s_http.GetAsync(uri, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            if (response.Content.Headers.ContentLength is > MaxBytes)
                return null;

            byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length is 0 or > MaxBytes)
                return null;

            string mediaType = response.Content.Headers.ContentType?.MediaType ?? "image/png";
            return Encode(bytes, mediaType);
        }
        // Only the caller giving up is a cancellation. HttpClient reports its own timeout as a
        // TaskCanceledException too, and treating that as one would let a single slow CDN response
        // poison the cache entry rather than simply producing no icon.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            lock (s_failedHosts)
            {
                s_failedHosts[uri.Host] = DateTime.UtcNow;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> LoadAsync(
        string key, string id, string? version, string? iconUrl, bool allowPackageDownload, CancellationToken ct)
    {
        string directory = Path.Combine(IconDirectory, NuGetPayloadService.Fingerprint(key));

        if (ReadCached(directory) is { } cached)
            return cached;

        if (HasFreshMiss(directory))
            return null;

        string? resolved = null;

        if (iconUrl is { Length: > 0 })
            resolved = await FromUrlAsync(iconUrl, ct);

        if (resolved is null && allowPackageDownload && version is { Length: > 0 })
        {
            var payload = await NuGetPayloadService.ReadAsync(id, version, ct);
            if (payload is { Icon.Length: > 0 } and { Icon.Length: <= MaxBytes })
                resolved = Encode(payload.Icon, payload.IconMediaType ?? "image/png");
        }

        WriteCached(directory, resolved);
        return resolved;
    }

    private static string? ReadCached(string directory)
    {
        try
        {
            string bytesPath = Path.Combine(directory, "icon.bin");
            string mimePath = Path.Combine(directory, "icon.mime");
            if (!File.Exists(bytesPath))
                return null;

            byte[] bytes = File.ReadAllBytes(bytesPath);
            string mediaType = File.Exists(mimePath) ? File.ReadAllText(mimePath).Trim() : "image/png";
            return bytes.Length == 0 ? null : Encode(bytes, mediaType);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Whether this package was recently found to have no icon. Without the marker, a feed that
    /// publishes no icons is re-probed for every package on every panel open, forever.
    /// </summary>
    private static bool HasFreshMiss(string directory)
    {
        try
        {
            string miss = Path.Combine(directory, "icon.miss");
            return File.Exists(miss) && DateTime.UtcNow - File.GetLastWriteTimeUtc(miss) < MissLifetime;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteCached(string directory, string? dataUri)
    {
        try
        {
            Directory.CreateDirectory(directory);

            if (dataUri is null)
            {
                File.WriteAllBytes(Path.Combine(directory, "icon.miss"), []);
                return;
            }

            int comma = dataUri.IndexOf(',');
            int semicolon = dataUri.IndexOf(';');
            if (comma < 0 || semicolon < 0)
                return;

            File.WriteAllBytes(
                Path.Combine(directory, "icon.bin"),
                Convert.FromBase64String(dataUri[(comma + 1)..]));
            File.WriteAllText(
                Path.Combine(directory, "icon.mime"),
                dataUri["data:".Length..semicolon]);
        }
        catch
        {
            // A cache that cannot be written still serves from memory for this process.
        }
    }

    private static string Encode(byte[] bytes, string mediaType) =>
        $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";
}

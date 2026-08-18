using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using RoslynMCP.Config;

namespace RoslynMCP.Services.ExternalSource;

/// <summary>The C# files under one directory of a repository, at one commit.</summary>
internal sealed record TreeIndex(string Commit, string Directory, ImmutableArray<string> Paths);

/// <summary>
/// Lists what is in a repository directory, so a type can be found by file name.
/// </summary>
/// <remarks>
/// <para>
/// Two API requests per assembly, ever. The key is a commit, which is immutable, so the answer is
/// cached on disk and never revalidated — which is what makes this work on the unauthenticated
/// budget of sixty requests an hour. Downloading the files themselves goes to a different host
/// with a far looser budget, and the two are kept apart so a failure to fetch a file cannot spend
/// or poison the API allowance.
/// </para>
/// </remarks>
internal static class GitHubTreeIndex
{
    private const long MaxTreeBytes = 32L * 1024 * 1024;

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = false };

    /// <summary>Serializes API calls: never burst against a sixty-an-hour budget.</summary>
    private static readonly SemaphoreSlim s_apiGate = new(1, 1);

    /// <summary>Coalesces concurrent navigations onto one lookup per directory.</summary>
    private static readonly ConcurrentDictionary<string, Lazy<Task<TreeIndex?>>> s_inFlight = new();

    /// <summary>Set when GitHub says the budget is spent, until it says it is refilled.</summary>
    private static DateTimeOffset s_rateLimitedUntil = DateTimeOffset.MinValue;

    /// <summary>
    /// The <c>.cs</c> files under <paramref name="directory"/> at <paramref name="commit"/>, or
    /// null when the directory does not exist or GitHub cannot be asked.
    /// </summary>
    public static Task<TreeIndex?> LoadAsync(
        string repository, string commit, string directory, CancellationToken ct)
    {
        string key = $"{repository}/{commit}/{directory}";

        var lazy = s_inFlight.GetOrAdd(
            key,
            _ => new Lazy<Task<TreeIndex?>>(
                () => BuildAsync(repository, commit, directory, ct),
                LazyThreadSafetyMode.ExecutionAndPublication));

        var task = lazy.Value;

        // A failed or empty lookup must not be remembered as the answer; the next navigation
        // should be free to try again once the network is back.
        _ = task.ContinueWith(
            completed =>
            {
                if (completed.IsFaulted || completed.Result is null)
                    s_inFlight.TryRemove(key, out _);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return task;
    }

    private static async Task<TreeIndex?> BuildAsync(
        string repository, string commit, string directory, CancellationToken ct)
    {
        if (Cached(repository, commit, directory) is { } cached)
            return cached;

        string? sha = await TopLevelShaAsync(repository, commit, directory, ct).ConfigureAwait(false);
        if (sha is null)
            return null;

        var paths = await SubtreeAsync(repository, sha, directory, ct).ConfigureAwait(false);
        if (paths is null)
            return null;

        var index = new TreeIndex(commit, directory, paths.Value);
        Store(repository, index);
        return index;
    }

    /// <summary>
    /// The sha of the top-level directory holding an assembly's sources.
    /// </summary>
    /// <remarks>
    /// The directories are named after assemblies, so the match is made against what the
    /// repository actually contains rather than a table that would need maintaining. That is also
    /// what makes the gaps behave: WPF and WinForms have no directory at any released commit, so
    /// they simply find nothing and fall through to decompilation with no special case.
    /// </remarks>
    private static async Task<string?> TopLevelShaAsync(
        string repository, string commit, string directory, CancellationToken ct)
    {
        var root = await GetJsonAsync(
            $"https://api.github.com/repos/{repository}/git/trees/{commit}", ct).ConfigureAwait(false);

        if (root is null || !root.Value.TryGetProperty("tree", out var entries))
            return null;

        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.TryGetProperty("type", out var type) && type.GetString() != "tree")
                continue;

            if (entry.TryGetProperty("path", out var path)
                && string.Equals(path.GetString(), directory, StringComparison.OrdinalIgnoreCase)
                && entry.TryGetProperty("sha", out var sha))
            {
                return sha.GetString();
            }
        }

        return null;
    }

    /// <summary>
    /// The files under a subtree, as paths from the repository root. The API answers relative to
    /// the subtree, but a path that cannot be fetched or cached as-is is a trap for every caller.
    /// </summary>
    private static async Task<ImmutableArray<string>?> SubtreeAsync(
        string repository, string sha, string directory, CancellationToken ct)
    {
        var tree = await GetJsonAsync(
            $"https://api.github.com/repos/{repository}/git/trees/{sha}?recursive=1", ct)
            .ConfigureAwait(false);

        if (tree is null || !tree.Value.TryGetProperty("tree", out var entries))
            return null;

        // A truncated listing is missing files with no indication of which, so a lookup against it
        // would report "no source" for a type that is present. Better to have no index at all.
        if (tree.Value.TryGetProperty("truncated", out var truncated) && truncated.GetBoolean())
        {
            ServiceLog.Warn(
                $"GitHub truncated the file listing for {repository}; reference source is unavailable "
                + "for this assembly.",
                key: $"github-truncated:{sha}");
            return null;
        }

        var paths = ImmutableArray.CreateBuilder<string>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.TryGetProperty("type", out var type) && type.GetString() != "blob")
                continue;

            if (entry.TryGetProperty("path", out var path)
                && path.GetString() is { } value
                && value.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                paths.Add($"{directory}/{value}");
            }
        }

        return paths.ToImmutable();
    }

    private static async Task<JsonElement?> GetJsonAsync(string url, CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < s_rateLimitedUntil)
            return null;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        await s_apiGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (DateTimeOffset.UtcNow < s_rateLimitedUntil)
                return null;

            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            if (LspFeatureOptions.GitHubToken is { Length: > 0 } token)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await HttpFetch
                .SendAsync(request, ct, HttpFetch.GitHubApi).ConfigureAwait(false);

            if (response is null)
                return null;

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                NoteRateLimit(response);
                return null;
            }

            if (!response.IsSuccessStatusCode)
                return null;

            if (response.Content.Headers.ContentLength > MaxTreeBytes)
                return null;

            byte[] content = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (content.Length > MaxTreeBytes)
                return null;

            // Parsed into a detached element so the document can be disposed with the response.
            using var document = JsonDocument.Parse(content);
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is JsonException)
        {
            return null;
        }
        finally
        {
            s_apiGate.Release();
        }
    }

    private static void NoteRateLimit(HttpResponseMessage response)
    {
        var until = DateTimeOffset.UtcNow.AddMinutes(10);

        if (response.Headers.TryGetValues("x-ratelimit-reset", out var values)
            && long.TryParse(values.FirstOrDefault(), out long epochSeconds))
        {
            until = DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
        }

        s_rateLimitedUntil = until;

        ServiceLog.Warn(
            $"GitHub's API budget is spent until {until.ToLocalTime():t}; .NET Framework navigation "
            + "decompiles until then. Set ROSLYNMCP_GITHUB_TOKEN to raise the limit.",
            key: "github-rate-limit");
    }

    private static string CachePath(string repository, string commit, string directory) =>
        Path.Combine(
            ExternalSourceCache.ReferenceSourceDirectory,
            "index",
            ExternalSourceCache.SanitizePathSegment(repository),
            commit,
            ExternalSourceCache.SanitizePathSegment(directory) + ".json");

    private static TreeIndex? Cached(string repository, string commit, string directory)
    {
        string path = CachePath(repository, commit, directory);
        try
        {
            if (!File.Exists(path))
                return null;

            var stored = JsonSerializer.Deserialize<StoredIndex>(File.ReadAllBytes(path));
            return stored?.Paths is null
                ? null
                : new TreeIndex(commit, directory, [.. stored.Paths]);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Store(string repository, TreeIndex index)
    {
        string path = CachePath(repository, index.Commit, index.Directory);
        var stored = new StoredIndex { Paths = [.. index.Paths] };

        ExternalSourceCache.WriteReadOnly(path, JsonSerializer.SerializeToUtf8Bytes(stored, s_json));
    }

    private sealed class StoredIndex
    {
        public List<string>? Paths { get; set; }
    }

    /// <summary>Forgets the in-memory lookups and any rate-limit hold. For tests.</summary>
    internal static void Reset()
    {
        s_inFlight.Clear();
        s_rateLimitedUntil = DateTimeOffset.MinValue;
    }
}

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using NuGet.Common;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace RoslynMCP.Services.Packages;

public sealed record PackageDependencyInfo(string Id, string VersionRange);

/// <param name="TargetFramework">A short folder name, or the empty string for the "any" group.</param>
public sealed record PackageDependencyGroupInfo(
    string TargetFramework,
    IReadOnlyList<PackageDependencyInfo> Dependencies);

public sealed record PackageDeprecationInfo(
    IReadOnlyList<string> Reasons,
    string? Message,
    string? AlternatePackageId,
    string? AlternateVersionRange);

public sealed record PackageVulnerabilityInfo(int Severity, string? AdvisoryUrl);

/// <summary>Everything the details pane shows about one version of a package.</summary>
public sealed record PackageMetadataDetail(
    string Id,
    string Version,
    string? Title,
    string? Description,
    string? Summary,
    string? Authors,
    string? Owners,
    string? Tags,
    long? Downloads,
    DateTimeOffset? Published,
    bool IsListed,
    bool PrefixReserved,
    bool RequireLicenseAcceptance,
    string? LicenseExpression,
    string? LicenseFileText,
    string? LicenseUrl,
    string? ProjectUrl,
    string? PackageDetailsUrl,
    string? ReportAbuseUrl,
    string? IconUrl,
    string? ReadmeMarkdown,
    IReadOnlyList<PackageDependencyGroupInfo> DependencyGroups,
    PackageDeprecationInfo? Deprecation,
    IReadOnlyList<PackageVulnerabilityInfo> Vulnerabilities,
    IReadOnlyList<string> AllVersions,
    string? SourceName);

/// <summary>
/// Package detail from the feed's registration index, plus the parts of it that only exist inside
/// the package.
/// </summary>
/// <remarks>
/// This is where deprecation notices and known vulnerabilities come from. They were reported as
/// "none" for every package before this existed, which meant the panel's warning banners could
/// never fire — the most misleading possible failure mode for a security signal.
///
/// Framework-gated: <c>DependencySets</c> is typed in terms of NuGet.Frameworks, which resolves
/// only through MSBuildLocator, so the projection to our own records runs behind
/// <see cref="WorkspaceService.EnsureRegistered"/> and never returns a NuGet.Frameworks type.
/// </remarks>
public static class NuGetMetadataService
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private const int MaxReadmeBytes = 256 * 1024;

    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static readonly ConcurrentDictionary<string, (DateTime FetchedUtc, Task<PackageMetadataDetail?> Task)> s_cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, Task<PackageMetadataResource?>> s_resources =
        new(StringComparer.OrdinalIgnoreCase);

    public static void Invalidate()
    {
        s_cache.Clear();
        // The resources are built from repositories that a config or credential change replaces,
        // so keeping them would keep talking to the old ones.
        s_resources.Clear();
    }

    /// <summary>
    /// Metadata for one package. With no <paramref name="version"/> the highest listed version is
    /// used, which is what the Browse tab wants.
    /// </summary>
    /// <param name="source">
    /// A feed name to read from, ignoring the rest. Without it the first feed that answers wins, so
    /// a panel scoped to one source could otherwise report a <c>SourceName</c> — and a version
    /// list — belonging to a different feed entirely.
    /// </param>
    public static Task<PackageMetadataDetail?> GetAsync(
        string id, string? version, bool includePrerelease, bool includeReadme, bool refresh,
        CancellationToken ct, string? source = null)
    {
        string key = $"{id}/{version ?? "latest"}/{includePrerelease}/{includeReadme}/{source ?? "*"}";

        if (refresh)
            s_cache.TryRemove(key, out _);
        // An exact (id, version) is immutable except for deprecation and vulnerabilities —
        // precisely the two fields that must not go stale, hence a TTL rather than a permanent
        // cache.
        // A canceled entry is as poisonous as a faulted one: the task captures the first caller's
        // token, so serving it again hands everyone else that cancellation for the whole TTL.
        else if (s_cache.TryGetValue(key, out var cached) &&
                 DateTime.UtcNow - cached.FetchedUtc < Lifetime &&
                 !(cached.Task.IsCompleted && !cached.Task.IsCompletedSuccessfully))
        {
            return cached.Task;
        }

        var task = LoadAsync(id, version, includePrerelease, includeReadme, refresh, source, ct);
        s_cache[key] = (DateTime.UtcNow, task);
        return task;
    }

    /// <summary>
    /// A package version's dependency groups, as short framework names.
    /// </summary>
    /// <remarks>
    /// Strings rather than <c>NuGetFramework</c> on purpose: a hazardous type in the signature is
    /// resolved when the caller is prepared, which would defeat the point of gating this at all.
    /// </remarks>
    public static async Task<IReadOnlyList<PackageDependencyGroupInfo>> DependencyGroupsAsync(
        string id, string version, CancellationToken ct)
    {
        var detail = await GetAsync(id, version, includePrerelease: true, includeReadme: false, refresh: false, ct);
        return detail?.DependencyGroups ?? [];
    }

    private static async Task<PackageMetadataDetail?> LoadAsync(
        string id, string? version, bool includePrerelease, bool includeReadme, bool refresh,
        string? source, CancellationToken ct)
    {
        try
        {
            WorkspaceService.EnsureRegistered();

            using var cache = NuGetFeedContext.RentCache(refresh);
            var (metadata, sourceName) = await FetchAsync(id, version, includePrerelease, source, cache, ct);
            if (metadata is null)
                return null;

            var projected = Project(metadata, sourceName);

            // Scoped to the same feed as the metadata: a version dropdown listing releases the
            // chosen feed does not serve would offer installs that cannot restore.
            var versions = await NuGetService.AllVersionsBySourceAsync(
                id, includePrerelease: true, refresh, ct);
            projected = projected with
            {
                AllVersions = NuGetService.Distinct(versions.Results, source)
                    .Select(v => v.ToNormalizedString())
                    .ToList(),
                Deprecation = await DeprecationAsync(metadata),
                ReadmeMarkdown = includeReadme ? await ReadmeFromFeedAsync(metadata, ct) : null,
            };

            return includeReadme ? await WithPackageContentAsync(projected, ct) : projected;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"Could not read metadata for {id}: {ex.Message}", key: $"nuget-metadata:{id}");
            return null;
        }
    }

    private static async Task<(IPackageSearchMetadata? Metadata, string? SourceName)> FetchAsync(
        string id, string? version, bool includePrerelease, string? source, SourceCacheContext cache,
        CancellationToken ct)
    {
        bool exact = version is { Length: > 0 } && NuGetVersion.TryParse(version, out _);
        NuGetVersion? parsed = exact ? NuGetVersion.Parse(version!) : null;

        foreach (var repository in NuGetFeedContext.Repositories(id))
        {
            if (source is { Length: > 0 } &&
                !repository.PackageSource.Name.Equals(source, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var pending = s_resources.GetOrAdd(
                    repository.PackageSource.Source,
                    _ => repository.GetResourceAsync<PackageMetadataResource>()!);

                // Building the resource fetches the feed's service index. A transient failure
                // there would otherwise be cached as a faulted task and rethrown on every later
                // call, leaving that feed dead until the process restarts.
                if (pending.IsCompleted && !pending.IsCompletedSuccessfully)
                {
                    s_resources.TryRemove(repository.PackageSource.Source, out _);
                    continue;
                }

                var resource = await pending;
                if (resource is null)
                    continue;

                if (parsed is not null)
                {
                    var one = await resource.GetMetadataAsync(
                        new PackageIdentity(id, parsed), cache, NullLogger.Instance, ct);
                    if (one is not null)
                        return (one, repository.PackageSource.Name);
                    continue;
                }

                var all = await resource.GetMetadataAsync(
                    id, includePrerelease, includeUnlisted: false, cache, NullLogger.Instance, ct);

                var latest = all
                    .OrderByDescending(m => m.Identity.Version)
                    .FirstOrDefault();

                if (latest is not null)
                    return (latest, repository.PackageSource.Name);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ServiceLog.Warn(
                    $"Metadata lookup failed on '{repository.PackageSource.Name}': {ex.Message}",
                    key: $"nuget-metadata-feed:{repository.PackageSource.Name}");
            }
        }

        return (null, null);
    }

    /// <summary>
    /// The README straight from the feed, where one is advertised — cheaper than pulling the whole
    /// package for it, which is the fallback.
    /// </summary>
    private static async Task<string?> ReadmeFromFeedAsync(IPackageSearchMetadata metadata, CancellationToken ct)
    {
        string? url = metadata.ReadmeFileUrl is { Length: > 0 } file ? file : metadata.ReadmeUrl?.ToString();
        if (url is not { Length: > 0 } ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        try
        {
            using var response = await s_http.GetAsync(uri, ct);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > MaxReadmeBytes)
                return null;

            string text = await response.Content.ReadAsStringAsync(ct);
            return text.Length > MaxReadmeBytes ? text[..MaxReadmeBytes] + "\n\n…" : text;
        }
        // Only the caller giving up. HttpClient's own timeout also surfaces as a
        // TaskCanceledException, and letting that escape would cache a cancellation for the TTL
        // over a README that was merely slow.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Fills in what the registration index does not carry: a README the feed exposes no URL for,
    /// and a license shipped as a file rather than an SPDX expression.
    /// </summary>
    private static async Task<PackageMetadataDetail> WithPackageContentAsync(
        PackageMetadataDetail detail, CancellationToken ct)
    {
        bool needsReadme = detail.ReadmeMarkdown is null;
        bool needsLicense = detail.LicenseExpression is null && detail.LicenseFileText is null;

        if (!needsReadme && !needsLicense)
            return detail;

        var payload = await NuGetPayloadService.ReadAsync(detail.Id, detail.Version, ct);
        if (payload is null)
            return detail;

        return detail with
        {
            ReadmeMarkdown = detail.ReadmeMarkdown ?? payload.Readme,
            LicenseFileText = detail.LicenseFileText ?? payload.LicenseText,
            LicenseExpression = detail.LicenseExpression ?? payload.LicenseExpression,
        };
    }

    // NoInlining: touching DependencySets or LicenseMetadata loads NuGet.Frameworks and
    // NuGet.Packaging, which resolve only after MSBuildLocator has registered.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PackageMetadataDetail Project(IPackageSearchMetadata metadata, string? sourceName)
    {
        var license = metadata.LicenseMetadata;

        return new PackageMetadataDetail(
            Id: metadata.Identity.Id,
            Version: metadata.Identity.Version.ToNormalizedString(),
            Title: Nullify(metadata.Title),
            Description: Nullify(metadata.Description),
            Summary: Nullify(metadata.Summary),
            Authors: Nullify(metadata.Authors),
            Owners: Nullify(metadata.Owners) ?? Join(metadata.OwnersList),
            Tags: Nullify(metadata.Tags),
            Downloads: metadata.DownloadCount,
            Published: metadata.Published,
            IsListed: metadata.IsListed,
            PrefixReserved: metadata.PrefixReserved,
            RequireLicenseAcceptance: metadata.RequireLicenseAcceptance,
            LicenseExpression: license is { Type: LicenseType.Expression } ? license.License : null,
            LicenseFileText: null,
            LicenseUrl: metadata.LicenseUrl?.ToString(),
            ProjectUrl: metadata.ProjectUrl?.ToString(),
            PackageDetailsUrl: metadata.PackageDetailsUrl?.ToString(),
            ReportAbuseUrl: metadata.ReportAbuseUrl?.ToString(),
            IconUrl: metadata.IconUrl?.ToString(),
            // The feed exposes a README URL only from NuGet 6.13 onward; older and private feeds
            // fall through to reading it out of the package.
            ReadmeMarkdown: null,
            DependencyGroups: metadata.DependencySets?
                .Select(group => new PackageDependencyGroupInfo(
                    group.TargetFramework?.GetShortFolderName() ?? "",
                    group.Packages?
                        .Select(p => new PackageDependencyInfo(p.Id, p.VersionRange?.ToNormalizedString() ?? ""))
                        .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                        .ToList() ?? []))
                .ToList() ?? [],
            Deprecation: null,
            Vulnerabilities: metadata.Vulnerabilities?
                .Select(v => new PackageVulnerabilityInfo(v.Severity, v.AdvisoryUrl?.ToString()))
                .OrderByDescending(v => v.Severity)
                .ToList() ?? [],
            AllVersions: [],
            SourceName: sourceName);
    }

    /// <summary>
    /// Deprecation is a second request against the feed, so it is resolved separately from the
    /// synchronous projection.
    /// </summary>
    internal static async Task<PackageDeprecationInfo?> DeprecationAsync(IPackageSearchMetadata metadata)
    {
        try
        {
            var deprecation = await metadata.GetDeprecationMetadataAsync();
            if (deprecation is null)
                return null;

            return new PackageDeprecationInfo(
                deprecation.Reasons?.ToList() ?? [],
                Nullify(deprecation.Message),
                Nullify(deprecation.AlternatePackage?.PackageId),
                Nullify(deprecation.AlternatePackage?.Range?.ToNormalizedString()));
        }
        catch
        {
            // A feed that does not implement the deprecation resource is not an error.
            return null;
        }
    }

    private static string? Join(IReadOnlyList<string>? values) =>
        values is { Count: > 0 } ? string.Join(", ", values) : null;

    private static string? Nullify(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

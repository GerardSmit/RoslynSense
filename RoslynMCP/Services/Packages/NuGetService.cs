using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using RoslynMCP.Services.ProjectModel;

namespace RoslynMCP.Services.Packages;

public sealed record PackageSummary(
    string Id,
    string Version,
    string? Authors,
    string? Description,
    long? Downloads,
    string? IconUrl,
    bool Deprecated,
    bool Vulnerable,
    string? InstalledVersion);

public sealed record ProjectPackages(
    string ProjectPath,
    string ProjectName,
    IReadOnlyList<PackageSummary> Packages);

public sealed record PackageVersionUse(string ProjectPath, string ProjectName, string Version);

public sealed record Consolidation(string Id, IReadOnlyList<PackageVersionUse> Versions);

public sealed record PackageOperationResult(bool Success, string Message);

/// <summary>
/// The NuGet client behind the package panel and the AI's package tools.
///
/// It lives in the daemon rather than the webview for a concrete reason: private feeds need
/// NuGet.config credentials and credential providers, which a webview cannot supply and should
/// never see. Mutations shell the dotnet CLI so that authentication, Central Package
/// Management, and lock files behave exactly as they do on the command line.
/// </summary>
public static class NuGetService
{
    private static readonly SourceCacheContext s_cache = new();
    private static readonly ConcurrentDictionary<string, SourceRepository> s_repositories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Configured, enabled sources for the loaded solution.</summary>
    public static IReadOnlyList<string> Sources()
    {
        try
        {
            var settings = LoadSettings();
            return new PackageSourceProvider(settings)
                .LoadPackageSources()
                .Where(source => source.IsEnabled)
                .Select(source => source.Name)
                .ToList();
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"Could not read NuGet sources: {ex.Message}", key: "nuget-sources");
            return [];
        }
    }

    public static async Task<IReadOnlyList<PackageSummary>> SearchAsync(
        string query, bool includePrerelease, int skip, int take, CancellationToken ct)
    {
        var installed = await InstalledVersionsAsync(ct);
        var results = new List<PackageSummary>();

        foreach (var repository in Repositories())
        {
            try
            {
                var search = await repository.GetResourceAsync<PackageSearchResource>(ct);
                if (search is null)
                    continue;

                var found = await search.SearchAsync(
                    query, new SearchFilter(includePrerelease), skip, take, NullLogger.Instance, ct);

                foreach (var package in found)
                {
                    results.Add(new PackageSummary(
                        package.Identity.Id,
                        package.Identity.Version.ToNormalizedString(),
                        package.Authors,
                        package.Description,
                        package.DownloadCount is { } downloads ? (long)downloads : null,
                        package.IconUrl?.ToString(),
                        Deprecated: false,
                        Vulnerable: false,
                        installed.GetValueOrDefault(package.Identity.Id)));
                }
            }
            catch (Exception ex)
            {
                ServiceLog.Warn(
                    $"Search failed on '{repository.PackageSource.Name}': {ex.Message}",
                    key: $"nuget-search:{repository.PackageSource.Name}");
            }
        }

        return results
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    public static async Task<IReadOnlyList<string>> VersionsAsync(
        string id, bool includePrerelease, CancellationToken ct)
    {
        var versions = new List<NuGetVersion>();

        foreach (var repository in Repositories())
        {
            try
            {
                var finder = await repository.GetResourceAsync<FindPackageByIdResource>(ct);
                if (finder is null)
                    continue;

                versions.AddRange(await finder.GetAllVersionsAsync(id, s_cache, NullLogger.Instance, ct));
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"Version lookup failed for '{id}': {ex.Message}", key: $"nuget-versions:{id}");
            }
        }

        return versions
            .Where(v => includePrerelease || !v.IsPrerelease)
            .Distinct()
            .OrderByDescending(v => v)
            .Select(v => v.ToNormalizedString())
            .ToList();
    }

    /// <summary>The download cache, shared so packages.config installs reuse it.</summary>
    internal static SourceCacheContext Cache => s_cache;

    /// <summary>
    /// The first feed that can hand over a .nupkg, for the packages.config installer — which
    /// unpacks the package itself rather than letting the CLI do it.
    /// </summary>
    internal static async Task<FindPackageByIdResource?> FindPackageResourceAsync(CancellationToken ct)
    {
        foreach (var repository in Repositories())
        {
            try
            {
                if (await repository.GetResourceAsync<FindPackageByIdResource>(ct) is { } resource)
                    return resource;
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"Feed unavailable: {ex.Message}", key: "nuget-find-resource");
            }
        }
        return null;
    }

    /// <summary>Direct package references per project, with their resolved versions.</summary>
    public static async Task<IReadOnlyList<ProjectPackages>> InstalledAsync(CancellationToken ct)
    {
        var solution = WorkspaceService.TryGetMostRecentSolution();
        if (solution is null)
            return [];

        var result = new List<ProjectPackages>();
        foreach (var project in solution.Projects)
        {
            if (project.FilePath is not { Length: > 0 } path)
                continue;

            // A packages.config project has no PackageReference items at all, so reading only the
            // evaluated model would report it as having no packages rather than as legacy.
            var packages = PackagesConfigService.Uses(path)
                ? PackagesConfigService.Read(path)
                    .Select(p => new PackageSummary(
                        p.Id, p.Version, Authors: null, Description: null, Downloads: null,
                        IconUrl: null, Deprecated: false, Vulnerable: false, InstalledVersion: p.Version))
                    .ToList()
                : null;

            if (packages is null)
            {
                var evaluation = await ProjectEvaluationService.EvaluateAsync(path, ct);
                if (evaluation is null)
                    continue;

                packages = evaluation.PackageReferences
                    .Where(p => !p.IsImplicit)
                    .Select(p => new PackageSummary(
                        p.Id, p.Version ?? "", Authors: null, Description: null, Downloads: null,
                        IconUrl: null, Deprecated: false, Vulnerable: false, InstalledVersion: p.Version))
                    .ToList();
            }

            result.Add(new ProjectPackages(path, project.Name, packages));
        }

        return result
            .DistinctBy(p => p.ProjectPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Installed packages that have a newer version on a configured feed.</summary>
    public static async Task<IReadOnlyList<PackageSummary>> UpdatesAsync(
        bool includePrerelease, CancellationToken ct)
    {
        var installed = await InstalledVersionsAsync(ct);
        var updates = new List<PackageSummary>();

        foreach (var (id, current) in installed)
        {
            ct.ThrowIfCancellationRequested();

            var versions = await VersionsAsync(id, includePrerelease, ct);
            if (versions.Count == 0)
                continue;

            if (NuGetVersion.TryParse(versions[0], out var latest) &&
                NuGetVersion.TryParse(current, out var installedVersion) &&
                latest > installedVersion)
            {
                updates.Add(new PackageSummary(
                    id, latest.ToNormalizedString(), null, null, null, null,
                    Deprecated: false, Vulnerable: false, InstalledVersion: current));
            }
        }

        return updates;
    }

    /// <summary>
    /// Packages referenced at more than one version across the solution — Rider's Consolidate
    /// tab, and the reason a package panel earns its keep in a multi-project repo.
    /// </summary>
    public static async Task<IReadOnlyList<Consolidation>> ConsolidationsAsync(CancellationToken ct)
    {
        var byProject = await InstalledAsync(ct);

        return byProject
            .SelectMany(project => project.Packages.Select(package =>
                new { package.Id, Use = new PackageVersionUse(project.ProjectPath, project.ProjectName, package.Version) }))
            .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group
                .Select(entry => entry.Use.Version)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() > 1)
            .Select(group => new Consolidation(group.Key, group.Select(entry => entry.Use).ToList()))
            .OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static Task<PackageOperationResult> InstallAsync(
        string id, string? version, IReadOnlyList<string> projectPaths, CancellationToken ct) =>
        PerProjectAsync(
            projectPaths,
            legacy: project => PackagesConfigService.InstallAsync(project, id, version, ct),
            modern: project =>
            {
                var args = new List<string> { "add", project, "package", id };
                if (!string.IsNullOrWhiteSpace(version))
                {
                    args.Add("--version");
                    args.Add(version);
                }
                return args;
            },
            $"Installed {id}", ct);

    public static Task<PackageOperationResult> UninstallAsync(
        string id, IReadOnlyList<string> projectPaths, CancellationToken ct) =>
        PerProjectAsync(
            projectPaths,
            legacy: project => PackagesConfigService.UninstallAsync(project, id, ct),
            modern: project => ["remove", project, "package", id],
            $"Removed {id}", ct);

    /// <summary>
    /// Splits a package operation by project format: packages.config projects are handled here,
    /// the rest by the dotnet CLI.
    /// </summary>
    /// <remarks>
    /// The CLI rejects a packages.config project outright, so routing by format is the difference
    /// between the panel working on a legacy solution and reporting an opaque failure.
    /// </remarks>
    private static async Task<PackageOperationResult> PerProjectAsync(
        IReadOnlyList<string> projectPaths,
        Func<string, Task<PackageOperationResult>> legacy,
        Func<string, IReadOnlyList<string>> modern,
        string successMessage,
        CancellationToken ct)
    {
        // Splitting an empty list leaves both halves empty, which would otherwise read as
        // "everything succeeded" rather than "nothing was selected".
        if (projectPaths.Count == 0)
            return new PackageOperationResult(false, "No project selected.");

        var legacyProjects = projectPaths.Where(PackagesConfigService.Uses).ToList();
        var modernProjects = projectPaths.Except(legacyProjects, StringComparer.OrdinalIgnoreCase).ToList();

        var failures = new List<string>();

        foreach (string project in legacyProjects)
        {
            var result = await legacy(project);
            if (!result.Success)
                failures.Add($"{Path.GetFileName(project)}: {result.Message}");
        }

        if (modernProjects.Count > 0)
        {
            var result = await RunPerProjectAsync(modernProjects, modern, successMessage, ct);
            if (!result.Success)
                failures.Add(result.Message);
        }

        return failures.Count == 0
            ? new PackageOperationResult(true, $"{successMessage} in {projectPaths.Count} project(s).")
            : new PackageOperationResult(false, string.Join("; ", failures));
    }

    /// <summary>
    /// Aligns every project that references <paramref name="id"/> onto one version. Under
    /// Central Package Management the version lives in Directory.Packages.props, so `dotnet add
    /// package` writes there and leaves the csproj references version-less — which is exactly
    /// why this goes through the CLI rather than editing XML directly.
    /// </summary>
    public static async Task<PackageOperationResult> ConsolidateAsync(
        string id, string version, CancellationToken ct)
    {
        var installed = await InstalledAsync(ct);
        var projects = installed
            .Where(p => p.Packages.Any(pkg => pkg.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.ProjectPath)
            .ToList();

        return projects.Count == 0
            ? new PackageOperationResult(false, $"No project references {id}.")
            : await InstallAsync(id, version, projects, ct);
    }

    private static async Task<PackageOperationResult> RunPerProjectAsync(
        IReadOnlyList<string> projectPaths,
        Func<string, IReadOnlyList<string>> argumentsFor,
        string successMessage,
        CancellationToken ct)
    {
        if (projectPaths.Count == 0)
            return new PackageOperationResult(false, "No project selected.");

        await using var progress = await ProgressReporter.BeginAsync(successMessage, ct);

        var failures = new List<string>();
        foreach (string project in projectPaths)
        {
            progress.Report(Path.GetFileNameWithoutExtension(project));

            var (exitCode, output) = await RunDotnetAsync(argumentsFor(project), ct);
            if (exitCode != 0)
                failures.Add($"{Path.GetFileName(project)}: {FirstLine(output)}");

            ProjectEvaluationService.Evict(project);
        }

        // The package set changed, so every cached snapshot and analyzer result is stale.
        await WorkspaceService.EvictAllAsync(ct);

        return failures.Count == 0
            ? new PackageOperationResult(true, $"{successMessage} in {projectPaths.Count} project(s).")
            : new PackageOperationResult(false, string.Join("; ", failures));
    }

    private static async Task<(int ExitCode, string Output)> RunDotnetAsync(
        IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo);
        if (process is null)
            return (-1, "Failed to start dotnet.");

        string stdout = await process.StandardOutput.ReadToEndAsync(ct);
        string stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return (process.ExitCode, stderr.Length > 0 ? stderr : stdout);
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.Contains("error", StringComparison.OrdinalIgnoreCase))
        ?? text.Split('\n').FirstOrDefault()?.Trim()
        ?? "failed";

    /// <summary>Package id → the version installed somewhere in the solution.</summary>
    private static async Task<Dictionary<string, string>> InstalledVersionsAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in await InstalledAsync(ct))
        {
            foreach (var package in project.Packages)
                map.TryAdd(package.Id, package.Version);
        }
        return map;
    }

    private static IEnumerable<SourceRepository> Repositories()
    {
        PackageSource[] sources;
        try
        {
            sources = new PackageSourceProvider(LoadSettings())
                .LoadPackageSources()
                .Where(source => source.IsEnabled)
                .ToArray();
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"Could not load NuGet sources: {ex.Message}", key: "nuget-load");
            yield break;
        }

        foreach (var source in sources)
        {
            yield return s_repositories.GetOrAdd(
                source.Source, _ => Repository.Factory.GetCoreV3(source));
        }
    }

    private static ISettings LoadSettings()
    {
        string? root = Path.GetDirectoryName(WorkspaceService.TryGetMostRecentSolution()?.FilePath)
            ?? Directory.GetCurrentDirectory();
        return Settings.LoadDefaultSettings(root);
    }

    /// <summary>
    /// Reads a package icon and returns it as a data URI. The webview's CSP forbids remote
    /// images outright, so proxying here is what makes icons possible at all; oversized ones
    /// are dropped rather than inlined.
    /// </summary>
    public static async Task<string?> IconDataUriAsync(string url, CancellationToken ct)
    {
        const int maxBytes = 256 * 1024;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            return null;

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var response = await client.GetAsync(uri, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            if (response.Content.Headers.ContentLength is > maxBytes)
                return null;

            byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > maxBytes)
                return null;

            string mediaType = response.Content.Headers.ContentType?.MediaType ?? "image/png";
            return $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Transitive dependencies from project.assets.json — resolved without a restore,
    /// and recording which direct reference pulled each one in.</summary>
    public static IReadOnlyList<(string Id, string Version, string BroughtInBy)> Transitive(string projectPath)
    {
        string assets = Path.Combine(Path.GetDirectoryName(projectPath) ?? "", "obj", "project.assets.json");
        if (!File.Exists(assets))
            return [];

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(assets));
            if (!document.RootElement.TryGetProperty("targets", out var targets))
                return [];

            var result = new List<(string, string, string)>();
            foreach (var target in targets.EnumerateObject())
            {
                foreach (var package in target.Value.EnumerateObject())
                {
                    var parts = package.Name.Split('/');
                    if (parts.Length != 2 || !package.Value.TryGetProperty("dependencies", out var dependencies))
                        continue;

                    foreach (var dependency in dependencies.EnumerateObject())
                        result.Add((dependency.Name, dependency.Value.GetString() ?? "", parts[0]));
                }
                break; // one target framework is enough for a dependency listing
            }
            return result;
        }
        catch
        {
            return [];
        }
    }
}

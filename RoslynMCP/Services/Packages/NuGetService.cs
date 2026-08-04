using System.Diagnostics;
using NuGet.Common;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using RoslynMCP.Services.ProjectModel;

namespace RoslynMCP.Services.Packages;

/// <param name="InstalledVersion">
/// The highest version installed anywhere in the solution, or <c>null</c> when the package is not
/// installed. <paramref name="InstalledVersions"/> carries the rest when projects disagree.
/// </param>
public sealed record PackageSummary(
    string Id,
    string Version,
    string? Authors,
    string? Description,
    long? Downloads,
    string? IconUrl,
    bool Deprecated,
    bool Vulnerable,
    string? InstalledVersion,
    IReadOnlyList<string>? InstalledVersions = null,
    bool IsCentrallyManaged = false,
    bool IsGlobalPackageReference = false,
    string? VersionSource = null,
    string? SourceName = null);

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
///
/// Feed access itself is <see cref="NuGetFeedContext"/>'s job, so that source mapping, per-source
/// failure reporting and credentials are decided in one place rather than per call site.
/// </summary>
public static class NuGetService
{
    /// <summary>Configured sources for the loaded solution, disabled ones included.</summary>
    public static IReadOnlyList<PackageSourceInfo> Sources() => NuGetFeedContext.Sources();

    public static async Task<FeedResults<PackageSummary>> SearchAsync(
        string query, bool includePrerelease, int skip, int take, string? source, CancellationToken ct)
    {
        var installed = await InstalledVersionsAsync(ct);
        using var cache = NuGetFeedContext.RentCache();

        var found = await NuGetFeedContext.FanOutAsync<(PackageSummary Summary, NuGetVersion Version)>(
            packageId: null,
            async (repository, token) =>
            {
                if (source is { Length: > 0 } &&
                    !repository.PackageSource.Name.Equals(source, StringComparison.OrdinalIgnoreCase))
                {
                    return [];
                }

                var search = await repository.GetResourceAsync<PackageSearchResource>(token);
                if (search is null)
                    return [];

                var results = await search.SearchAsync(
                    query, new SearchFilter(includePrerelease), skip, take, NullLogger.Instance, token);

                return results.Select(package =>
                {
                    var versions = installed.GetValueOrDefault(package.Identity.Id);
                    return (
                        new PackageSummary(
                            package.Identity.Id,
                            package.Identity.Version.ToNormalizedString(),
                            package.Authors,
                            package.Description,
                            package.DownloadCount is { } downloads ? (long)downloads : null,
                            package.IconUrl?.ToString(),
                            Deprecated: false,
                            Vulnerable: false,
                            InstalledVersion: versions?.FirstOrDefault(),
                            InstalledVersions: versions,
                            SourceName: repository.PackageSource.Name),
                        package.Identity.Version);
                });
            },
            ct);

        // Several feeds can carry the same id; the highest version wins rather than whichever
        // feed happened to answer first.
        var packages = found.Results
            .GroupBy(entry => entry.Summary.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(entry => entry.Version).First().Summary)
            .ToList();

        return new FeedResults<PackageSummary>(packages, found.Feeds);
    }

    public static async Task<FeedResults<string>> VersionsAsync(
        string id, bool includePrerelease, bool refresh, CancellationToken ct)
    {
        var all = await AllVersionsAsync(id, includePrerelease, refresh, ct);

        return new FeedResults<string>(
            all.Results.Select(v => v.ToNormalizedString()).ToList(),
            all.Feeds);
    }

    /// <summary>Every version of a package the configured feeds know about, newest first.</summary>
    internal static async Task<FeedResults<NuGetVersion>> AllVersionsAsync(
        string id, bool includePrerelease, bool refresh, CancellationToken ct)
    {
        using var cache = NuGetFeedContext.RentCache(refresh);

        var found = await NuGetFeedContext.FanOutAsync<NuGetVersion>(
            id,
            async (repository, token) =>
            {
                var finder = await repository.GetResourceAsync<FindPackageByIdResource>(token);
                return finder is null
                    ? []
                    : await finder.GetAllVersionsAsync(id, cache, NullLogger.Instance, token);
            },
            ct);

        var versions = found.Results
            .Where(v => includePrerelease || !v.IsPrerelease)
            .Distinct()
            .OrderByDescending(v => v)
            .ToList();

        return new FeedResults<NuGetVersion>(versions, found.Feeds);
    }

    /// <summary>
    /// The first feed that can hand over a .nupkg, for the packages.config installer — which
    /// unpacks the package itself rather than letting the CLI do it.
    /// </summary>
    internal static async Task<FindPackageByIdResource?> FindPackageResourceAsync(
        string? packageId, CancellationToken ct)
    {
        foreach (var repository in NuGetFeedContext.Repositories(packageId))
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
    /// <remarks>
    /// The project list comes from <see cref="SolutionProjectIndex"/> rather than from Roslyn.
    /// A daemon that has just started has loaded nothing — the Solution Explorer is populated from
    /// the .sln on disk — so asking Roslyn returns an empty solution and the panel reports that
    /// there are no projects while the tree plainly shows six. Reading packages needs MSBuild
    /// evaluation, not a compilation, so there is nothing to wait for.
    /// </remarks>
    public static async Task<IReadOnlyList<ProjectPackages>> InstalledAsync(CancellationToken ct)
    {
        var result = new List<ProjectPackages>();

        foreach (string path in SolutionProjectIndex.ProjectPaths())
        {
            if (!File.Exists(path))
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
                        IconUrl: null, Deprecated: false, Vulnerable: false, InstalledVersion: p.Version,
                        InstalledVersions: p.Version is { Length: > 0 } ? [p.Version] : null,
                        IsCentrallyManaged: p.IsCentrallyManaged,
                        IsGlobalPackageReference: p.IsGlobalPackageReference,
                        VersionSource: p.VersionSource))
                    .ToList();
            }

            result.Add(new ProjectPackages(path, Path.GetFileNameWithoutExtension(path), packages));
        }

        return result
            .DistinctBy(p => p.ProjectPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
        string id, string? version, IReadOnlyList<string> projectPaths, CancellationToken ct,
        PackageMutationScope? scope = null) =>
        PerProjectAsync(
            projectPaths,
            legacy: (project, batch) => PackagesConfigService.InstallAsync(project, id, version, ct, batch),
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
            $"Installed {id}", ct, scope);

    public static Task<PackageOperationResult> UninstallAsync(
        string id, IReadOnlyList<string> projectPaths, CancellationToken ct,
        PackageMutationScope? scope = null) =>
        PerProjectAsync(
            projectPaths,
            legacy: (project, batch) => PackagesConfigService.UninstallAsync(project, id, ct, batch),
            modern: project => ["remove", project, "package", id],
            $"Removed {id}", ct, scope);

    /// <summary>
    /// Splits a package operation by project format: packages.config projects are handled here,
    /// the rest by the dotnet CLI.
    /// </summary>
    /// <remarks>
    /// The CLI rejects a packages.config project outright, so routing by format is the difference
    /// between the panel working on a legacy solution and reporting an opaque failure.
    /// </remarks>
    /// <remarks>
    /// The legacy callback takes the scope rather than closing over one, because the scope this
    /// method actually uses may be created here: a lambda built by the caller would capture the
    /// caller's <c>null</c>, and the packages.config path would then evict the workspace per
    /// project and never fire the editor refresh at all.
    /// </remarks>
    private static async Task<PackageOperationResult> PerProjectAsync(
        IReadOnlyList<string> projectPaths,
        Func<string, PackageMutationScope, Task<PackageOperationResult>> legacy,
        Func<string, IReadOnlyList<string>> modern,
        string successMessage,
        CancellationToken ct,
        PackageMutationScope? scope)
    {
        // Splitting an empty list leaves both halves empty, which would otherwise read as
        // "everything succeeded" rather than "nothing was selected".
        if (projectPaths.Count == 0)
            return new PackageOperationResult(false, "No project selected.");

        await using var owned = scope is null ? new PackageMutationScope(ct) : null;
        var effective = scope ?? owned!;

        var legacyProjects = projectPaths.Where(PackagesConfigService.Uses).ToList();
        var modernProjects = projectPaths.Except(legacyProjects, StringComparer.OrdinalIgnoreCase).ToList();

        var failures = new List<string>();

        foreach (string project in legacyProjects)
        {
            var result = await legacy(project, effective);
            if (result.Success)
                effective.Touch(project);
            else
                failures.Add($"{Path.GetFileName(project)}: {result.Message}");
        }

        if (modernProjects.Count > 0)
        {
            var result = await RunPerProjectAsync(modernProjects, modern, successMessage, ct, effective);
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
        CancellationToken ct,
        PackageMutationScope scope)
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

            scope.Touch(project);
        }

        return failures.Count == 0
            ? new PackageOperationResult(true, $"{successMessage} in {projectPaths.Count} project(s).")
            : new PackageOperationResult(false, string.Join("; ", failures));
    }

    internal static async Task<(int ExitCode, string Output)> RunDotnetAsync(
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

        // Both pipes are drained at once. Reading one to EOF first deadlocks as soon as the child
        // fills the other pipe's buffer — which a failing solution-wide restore easily does.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync(ct);

        string stdout = await stdoutTask;
        string stderr = await stderrTask;

        return (process.ExitCode, stderr.Length > 0 ? stderr : stdout);
    }

    internal static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.Contains("error", StringComparison.OrdinalIgnoreCase))
        ?? text.Split('\n').FirstOrDefault()?.Trim()
        ?? "failed";

    /// <summary>
    /// Package id to every version installed in the solution, highest first.
    /// </summary>
    /// <remarks>
    /// Plural on purpose: a solution mid-migration references the same package at two versions,
    /// and reporting one of them — whichever project enumeration reached first — made the Browse
    /// tab's "installed" badge nondeterministic.
    /// </remarks>
    internal static async Task<Dictionary<string, IReadOnlyList<string>>> InstalledVersionsAsync(
        CancellationToken ct)
    {
        var byId = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in await InstalledAsync(ct))
        {
            foreach (var package in project.Packages)
            {
                if (package.Version is not { Length: > 0 })
                    continue;

                if (!byId.TryGetValue(package.Id, out var versions))
                    byId[package.Id] = versions = new SortedSet<string>(VersionOrder.DescendingInstance);
                versions.Add(package.Version);
            }
        }

        return byId.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<string>)entry.Value.ToList(),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Highest first, with unparseable versions sorted last rather than dropped.</summary>
    private sealed class VersionOrder : IComparer<string>
    {
        public static readonly VersionOrder DescendingInstance = new();

        public int Compare(string? x, string? y)
        {
            bool xOk = NuGetVersion.TryParse(x, out var left);
            bool yOk = NuGetVersion.TryParse(y, out var right);

            if (xOk && yOk)
                return right.CompareTo(left);
            if (xOk != yOk)
                return xOk ? -1 : 1;
            return string.CompareOrdinal(x, y);
        }
    }
}

using RoslynMCP.Services;
using RoslynMCP.Services.Packages;
using RoslynMCP.Services.ProjectModel;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>
/// The package panel's server side.
/// </summary>
/// <remarks>
/// Icons and metadata are deliberately separate requests from the lists they belong to. Resolving
/// them inline meant a search response waited on thirty image downloads before the first row could
/// be drawn, which is why results used to appear long after the panel did.
/// </remarks>
internal static class NuGetHandler
{
    /// <summary>Wires the editor-facing side of a package change. Called once per session.</summary>
    public static void InstallMutationHook() =>
        PackageMutationScope.AfterMutation = ct =>
        {
            AnalyzerDiagnosticCache.Clear();
            return LspSessionRegistry.RequestRefreshAsync(RefreshKind.All, ct);
        };

    public static async Task<NuGetSearchResultDto> SearchAsync(NuGetSearchParams p, CancellationToken ct)
    {
        var found = await NuGetService.SearchAsync(
            p.Query, p.IncludePrerelease, p.Skip, p.Take, p.Source, ct);

        return new NuGetSearchResultDto(
            found.Results.Select(ToDto).ToArray(),
            found.Feeds.Select(ToDto).ToArray());
    }

    public static async Task<NuGetVersionsResultDto> VersionsAsync(NuGetVersionsParams p, CancellationToken ct)
    {
        var found = await NuGetService.VersionsAsync(p.Id, p.IncludePrerelease, p.Refresh, ct);
        return new NuGetVersionsResultDto(found.Results.ToArray(), found.Feeds.Select(ToDto).ToArray());
    }

    public static async Task<ProjectPackagesDto[]> InstalledAsync(CancellationToken ct)
    {
        var projects = await NuGetService.InstalledAsync(ct);

        var dtos = new List<ProjectPackagesDto>(projects.Count);
        foreach (var project in projects)
        {
            var evaluation = await ProjectEvaluationService.EvaluateAsync(project.ProjectPath, ct);
            dtos.Add(new ProjectPackagesDto(
                project.ProjectPath,
                project.ProjectName,
                evaluation?.TargetFrameworks.ToArray() ?? [],
                project.Packages.Select(ToDto).ToArray()));
        }

        return dtos.ToArray();
    }

    public static async Task<NuGetUpdatesResultDto> UpdatesAsync(NuGetUpdatesParams p, CancellationToken ct)
    {
        var query = new UpdateQuery(
            IncludePrerelease: p.IncludePrerelease,
            Lock: ParseLock(p.VersionLock),
            Prerelease: ParsePrerelease(p.Prerelease),
            ProjectPaths: p.ProjectPaths,
            Refresh: p.Refresh,
            AlignPlatform: p.AlignPlatform);

        var found = await PackageUpdateService.OutdatedAsync(query, ct);

        return new NuGetUpdatesResultDto(
            found.Results
                .Select(u => new PackageUpdateDto(
                    u.Id, u.CurrentVersion, u.LatestVersion, u.Severity.ToString().ToLowerInvariant(),
                    u.ProjectPath, u.ProjectName, u.IsCentrallyManaged, u.IsGlobalPackageReference,
                    u.VersionSource, u.LatestUncapped))
                .ToArray(),
            found.Feeds.Select(ToDto).ToArray());
    }

    public static async Task<ConsolidationDto[]> ConsolidationsAsync(CancellationToken ct)
    {
        var consolidations = await NuGetService.ConsolidationsAsync(ct);
        return consolidations
            .Select(c => new ConsolidationDto(
                c.Id,
                c.Versions.Select(v => new PackageVersionUseDto(v.ProjectPath, v.ProjectName, v.Version)).ToArray()))
            .ToArray();
    }

    public static async Task<NuGetIconDto> IconAsync(NuGetIconParams p, CancellationToken ct)
    {
        var dataUri = await NuGetIconService.ResolveAsync(p.Id, p.Version, p.IconUrl, p.AllowDownload, ct);
        return new NuGetIconDto(p.Id, dataUri);
    }

    public static async Task<PackageMetadataDto?> MetadataAsync(NuGetMetadataParams p, CancellationToken ct)
    {
        var detail = await NuGetMetadataService.GetAsync(
            p.Id, p.Version, p.IncludePrerelease, p.IncludeReadme, p.Refresh, ct);

        return detail is null ? null : ToDto(detail);
    }

    /// <summary>
    /// Whether a package version supports what the selected projects target. Advisory: the answer
    /// is shown before an install, never used to refuse one.
    /// </summary>
    public static async Task<NuGetFrameworkCheckDto> CheckFrameworkAsync(
        NuGetFrameworkCheckParams p, CancellationToken ct)
    {
        var mismatches = new List<FrameworkMismatchDto>();
        IReadOnlyList<string> packageFrameworks = [];

        foreach (string projectPath in p.ProjectPaths ?? [])
        {
            var frameworks = await PackageFrameworkService.FrameworksOfAsync(projectPath, ct);
            if (frameworks.Count == 0)
                continue;

            var result = await PackageFrameworkService.CheckAsync(p.Id, p.Version, frameworks, ct);
            packageFrameworks = result.PackageFrameworks;

            if (!result.Compatible)
            {
                mismatches.Add(new FrameworkMismatchDto(
                    projectPath,
                    Path.GetFileNameWithoutExtension(projectPath),
                    result.UnsupportedFrameworks.ToArray()));
            }
        }

        string? warning = mismatches.Count == 0
            ? null
            : $"{p.Id} {p.Version} supports {string.Join(", ", packageFrameworks)}, which does not cover " +
              string.Join("; ", mismatches.Select(m => $"{m.ProjectName} ({string.Join(", ", m.TargetFrameworks)})")) +
              ". Installing it will fail to restore unless the package ships assets another way.";

        return new NuGetFrameworkCheckDto(
            mismatches.Count == 0, mismatches.ToArray(), packageFrameworks.ToArray(), warning);
    }

    public static async Task<PackageOperationDto> InstallAsync(NuGetOperationParams p, CancellationToken ct)
    {
        var result = await NuGetService.InstallAsync(p.Id, p.Version, p.ProjectPaths ?? [], ct);
        AfterMutation();
        return new PackageOperationDto(result.Success, result.Message);
    }

    public static async Task<PackageOperationDto> UninstallAsync(NuGetOperationParams p, CancellationToken ct)
    {
        var result = await NuGetService.UninstallAsync(p.Id, p.ProjectPaths ?? [], ct);
        AfterMutation();
        return new PackageOperationDto(result.Success, result.Message);
    }

    public static async Task<PackageOperationDto> ConsolidateAsync(NuGetOperationParams p, CancellationToken ct)
    {
        var result = await NuGetService.ConsolidateAsync(p.Id, p.Version ?? "", ct);
        AfterMutation();
        return new PackageOperationDto(result.Success, result.Message);
    }

    /// <summary>
    /// What else a selection would have to move, before anything is written.
    /// </summary>
    /// <remarks>
    /// A separate request rather than a flag on the update: the induced list is shown for
    /// confirmation, and a package the user did not choose is not one to edit on their behalf.
    /// </remarks>
    public static async Task<NuGetUpdatePlanResultDto> UpdatePlanAsync(
        NuGetUpdatePlanParams p, CancellationToken ct)
    {
        var requests = p.Packages
            .Select(item => new PackageUpdateRequest(item.Id, item.Version, item.ProjectPaths))
            .ToList();

        var induced = await PackageDependencyPlanner.PlanAsync(
            requests,
            ParseMode(p.Mode),
            new UpdateQuery(
                IncludePrerelease: p.IncludePrerelease,
                Lock: ParseLock(p.VersionLock),
                AlignPlatform: p.AlignPlatform),
            ct);

        return new NuGetUpdatePlanResultDto(
            induced
                .Select(u => new InducedUpdateDto(
                    u.Id, u.CurrentVersion, u.Version, u.ProjectPath, u.ProjectName,
                    u.RequiredBy, u.RequiredByVersion))
                .ToArray());
    }

    public static async Task<NuGetUpdateAllResultDto> UpdateAllAsync(
        NuGetUpdateAllParams p, CancellationToken ct)
    {
        var requests = p.Packages
            .Select(item => new PackageUpdateRequest(item.Id, item.Version, item.ProjectPaths))
            .ToList();

        var result = await PackageUpdateService.UpdateAllAsync(requests, p.Restore, ct);
        AfterMutation();

        return new NuGetUpdateAllResultDto(
            result.Success,
            result.Message,
            result.Results
                .Select(r => new NuGetUpdateOutcomeDto(r.Id, r.Version, r.ProjectPath, r.Success, r.Message))
                .ToArray());
    }

    public static NuGetTransitiveDto Transitive(NuGetTransitiveParams p)
    {
        var graph = ProjectAssetsService.Read(p.ProjectPath);

        var packages = p.PackageId is { Length: > 0 }
            ? ProjectAssetsService.DependenciesOf(p.ProjectPath, p.PackageId, p.TargetFramework)
            : ProjectAssetsService.TransitiveOnly(p.ProjectPath, p.TargetFramework);

        return new NuGetTransitiveDto(
            p.ProjectPath,
            graph.TargetFrameworks.ToArray(),
            packages
                .Select(package => new TransitivePackageDto(
                    package.Id, package.Version, package.TargetFramework, package.Dependencies.Count > 0))
                .ToArray());
    }

    public static async Task<NuGetAuditDto> AuditAsync(NuGetAuditParams p, CancellationToken ct)
    {
        var audit = await PackageAuditService.AuditAsync(p.Refresh, ct);

        return new NuGetAuditDto(
            audit.Vulnerabilities
                .Select(v => new PackageAdvisoryDto(
                    v.Id, v.Version, v.ProjectPath, v.TargetFramework, v.IsTransitive, v.Severity, v.AdvisoryUrl))
                .ToArray(),
            audit.Deprecations
                .Select(d => new PackageDeprecationEntryDto(
                    d.Id, d.Version, d.ProjectPath, d.TargetFramework, d.IsTransitive,
                    d.Reasons.ToArray(), d.AlternatePackageId, d.AlternateVersionRange))
                .ToArray(),
            audit.Error);
    }

    /// <summary>
    /// Adds, retargets, removes, enables or reorders a feed, then hands back the whole list so the
    /// panel never has to guess what the config chain settled on.
    /// </summary>
    public static NuGetSourceEditResultDto EditSources(NuGetSourceEditParams p)
    {
        var result = p.Action.ToLowerInvariant() switch
        {
            "add" => NuGetFeedContext.AddSource(p.Name ?? "", p.Source ?? ""),
            "update" => NuGetFeedContext.UpdateSource(p.Name ?? "", p.NewName, p.Source),
            "remove" => NuGetFeedContext.RemoveSource(p.Name ?? ""),
            "enable" => NuGetFeedContext.SetSourceEnabled(p.Name ?? "", true),
            "disable" => NuGetFeedContext.SetSourceEnabled(p.Name ?? "", false),
            "reorder" => NuGetFeedContext.ReorderSources(p.Order ?? []),
            _ => new PackageOperationResult(false, $"Unknown action '{p.Action}'."),
        };

        // A feed change invalidates every cached answer that came from one — including the project
        // file squiggles, whose whole job is to distinguish "no such version" from "no feed
        // answered". Leaving them would keep reporting the state of a feed list that no longer
        // exists, and a fixed credential would look like it changed nothing.
        if (result.Success)
        {
            PackageUpdateService.Invalidate();
            NuGetMetadataService.Invalidate();
            Languages.MsBuild.Core.PackageStatusCache.Invalidate();
        }

        return new NuGetSourceEditResultDto(result.Success, result.Message, Sources());
    }

    public static PackageSourceDto[] Sources() =>
        NuGetService.Sources()
            .Select(s => new PackageSourceDto(
                s.Name, s.Source, s.IsEnabled, s.IsMachineWide, s.IsLocal, s.HasCredentials, s.ConfigFilePath))
            .ToArray();

    /// <summary>A package change invalidates every audit and update answer we are holding.</summary>
    /// <remarks>
    /// The project-file squiggles are not dropped here. What a mutation changes is which version a
    /// file names, not what the feeds say about any version — and the file itself is about to be
    /// rewritten, which invalidates its parse and re-derives them anyway. Clearing the status cache
    /// would throw away several hundred correct entries to no purpose.
    /// </remarks>
    private static void AfterMutation()
    {
        PackageAuditService.Invalidate();
        PackageUpdateService.Invalidate();
        ProjectAssetsService.Invalidate();
    }

    private static VersionLock ParseLock(string? value) => value?.ToLowerInvariant() switch
    {
        "major" => VersionLock.Major,
        "minor" => VersionLock.Minor,
        "framework" => VersionLock.Framework,
        _ => VersionLock.None,
    };

    private static DependencyUpdateMode ParseMode(string? value) => value?.ToLowerInvariant() switch
    {
        "minimal" => DependencyUpdateMode.Minimal,
        "latest" => DependencyUpdateMode.Latest,
        _ => DependencyUpdateMode.SelectedOnly,
    };

    private static PrereleaseReporting ParsePrerelease(string? value) => value?.ToLowerInvariant() switch
    {
        "always" => PrereleaseReporting.Always,
        "never" => PrereleaseReporting.Never,
        _ => PrereleaseReporting.Auto,
    };

    private static FeedOutcomeDto ToDto(FeedOutcome feed) =>
        new(feed.Name, feed.Source, feed.Ok, feed.Unauthorized, feed.Error);

    private static PackageSummaryDto ToDto(PackageSummary package) =>
        new(package.Id, package.Version, package.Authors, package.Description, package.Downloads,
            package.IconUrl, package.Deprecated, package.Vulnerable, package.InstalledVersion,
            package.InstalledVersions?.ToArray() ?? [],
            package.IsCentrallyManaged, package.IsGlobalPackageReference,
            package.VersionSource, package.SourceName);

    private static PackageMetadataDto ToDto(PackageMetadataDetail detail) =>
        new(detail.Id, detail.Version, detail.Title, detail.Description, detail.Summary,
            detail.Authors, detail.Owners, detail.Tags, detail.Downloads,
            detail.Published?.ToString("O"), detail.IsListed, detail.PrefixReserved,
            detail.RequireLicenseAcceptance, detail.LicenseExpression, detail.LicenseFileText,
            detail.LicenseUrl, detail.ProjectUrl, detail.PackageDetailsUrl, detail.ReportAbuseUrl,
            detail.IconUrl, detail.ReadmeMarkdown,
            detail.DependencyGroups
                .Select(g => new PackageDependencyGroupDto(
                    g.TargetFramework,
                    g.Dependencies.Select(d => new PackageDependencyDto(d.Id, d.VersionRange)).ToArray()))
                .ToArray(),
            detail.Deprecation is { } deprecation
                ? new PackageDeprecationDto(
                    deprecation.Reasons.ToArray(), deprecation.Message,
                    deprecation.AlternatePackageId, deprecation.AlternateVersionRange)
                : null,
            detail.Vulnerabilities
                .Select(v => new PackageVulnerabilityDto(v.Severity, v.AdvisoryUrl))
                .ToArray(),
            detail.AllVersions.ToArray(),
            detail.SourceName);
}

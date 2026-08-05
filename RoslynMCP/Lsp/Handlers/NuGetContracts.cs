using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Handlers;

// ---- Requests ---------------------------------------------------------------------------

public sealed record NuGetSearchParams(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("includePrerelease")] bool IncludePrerelease = false,
    [property: JsonPropertyName("skip")] int Skip = 0,
    [property: JsonPropertyName("take")] int Take = 30,
    [property: JsonPropertyName("source")] string? Source = null);

public sealed record NuGetVersionsParams(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("includePrerelease")] bool IncludePrerelease = false,
    [property: JsonPropertyName("refresh")] bool Refresh = false);

public sealed record NuGetUpdatesParams(
    [property: JsonPropertyName("includePrerelease")] bool IncludePrerelease = false,
    [property: JsonPropertyName("versionLock")] string? VersionLock = null,
    [property: JsonPropertyName("prerelease")] string? Prerelease = null,
    [property: JsonPropertyName("projectPaths")] string[]? ProjectPaths = null,
    [property: JsonPropertyName("refresh")] bool Refresh = false,
    [property: JsonPropertyName("alignPlatform")] bool AlignPlatform = true);

public sealed record NuGetOperationParams(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string? Version = null,
    [property: JsonPropertyName("projectPaths")] string[]? ProjectPaths = null);

public sealed record NuGetIconParams(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string? Version = null,
    [property: JsonPropertyName("iconUrl")] string? IconUrl = null,
    [property: JsonPropertyName("allowDownload")] bool AllowDownload = false);

public sealed record NuGetMetadataParams(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string? Version = null,
    [property: JsonPropertyName("includePrerelease")] bool IncludePrerelease = false,
    [property: JsonPropertyName("includeReadme")] bool IncludeReadme = true,
    [property: JsonPropertyName("refresh")] bool Refresh = false);

public sealed record NuGetFrameworkCheckParams(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("projectPaths")] string[]? ProjectPaths = null);

public sealed record NuGetUpdateItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("projectPaths")] string[] ProjectPaths);

public sealed record NuGetUpdateAllParams(
    [property: JsonPropertyName("packages")] NuGetUpdateItem[] Packages,
    [property: JsonPropertyName("restore")] bool Restore = true);

public sealed record NuGetUpdatePlanParams(
    [property: JsonPropertyName("packages")] NuGetUpdateItem[] Packages,
    [property: JsonPropertyName("mode")] string? Mode = null,
    [property: JsonPropertyName("versionLock")] string? VersionLock = null,
    [property: JsonPropertyName("includePrerelease")] bool IncludePrerelease = false,
    [property: JsonPropertyName("alignPlatform")] bool AlignPlatform = true);

public sealed record NuGetTransitiveParams(
    [property: JsonPropertyName("projectPath")] string ProjectPath,
    [property: JsonPropertyName("targetFramework")] string? TargetFramework = null,
    [property: JsonPropertyName("packageId")] string? PackageId = null);

public sealed record NuGetAuditParams(
    [property: JsonPropertyName("refresh")] bool Refresh = false);

/// <param name="Action">add | update | remove | enable | disable | reorder</param>
public sealed record NuGetSourceEditParams(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("newName")] string? NewName = null,
    [property: JsonPropertyName("source")] string? Source = null,
    [property: JsonPropertyName("order")] string[]? Order = null);

// ---- Results ----------------------------------------------------------------------------

public sealed record PackageSummaryDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("authors")] string? Authors,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("downloads")] long? Downloads,
    [property: JsonPropertyName("iconUrl")] string? IconUrl,
    [property: JsonPropertyName("deprecated")] bool Deprecated,
    [property: JsonPropertyName("vulnerable")] bool Vulnerable,
    [property: JsonPropertyName("installedVersion")] string? InstalledVersion,
    [property: JsonPropertyName("installedVersions")] string[] InstalledVersions,
    [property: JsonPropertyName("isCentrallyManaged")] bool IsCentrallyManaged,
    [property: JsonPropertyName("isGlobalPackageReference")] bool IsGlobalPackageReference,
    [property: JsonPropertyName("versionSource")] string? VersionSource,
    [property: JsonPropertyName("sourceName")] string? SourceName);

public sealed record ProjectPackagesDto(
    [property: JsonPropertyName("projectPath")] string ProjectPath,
    [property: JsonPropertyName("projectName")] string ProjectName,
    [property: JsonPropertyName("targetFrameworks")] string[] TargetFrameworks,
    [property: JsonPropertyName("packages")] PackageSummaryDto[] Packages);

public sealed record ConsolidationDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("versions")] PackageVersionUseDto[] Versions);

public sealed record PackageVersionUseDto(
    [property: JsonPropertyName("projectPath")] string ProjectPath,
    [property: JsonPropertyName("projectName")] string ProjectName,
    [property: JsonPropertyName("version")] string Version);

public sealed record PackageOperationDto(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] string Message);

/// <summary>What one feed did. Lets the panel say "2 of 4 feeds answered" rather than showing a
/// short list that looks authoritative.</summary>
public sealed record FeedOutcomeDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("unauthorized")] bool Unauthorized,
    [property: JsonPropertyName("error")] string? Error);

public sealed record PackageSourceDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("isEnabled")] bool IsEnabled,
    [property: JsonPropertyName("isMachineWide")] bool IsMachineWide,
    [property: JsonPropertyName("isLocal")] bool IsLocal,
    [property: JsonPropertyName("hasCredentials")] bool HasCredentials,
    [property: JsonPropertyName("configFilePath")] string? ConfigFilePath);

public sealed record NuGetSourceEditResultDto(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("sources")] PackageSourceDto[] Sources);

public sealed record NuGetSearchResultDto(
    [property: JsonPropertyName("packages")] PackageSummaryDto[] Packages,
    [property: JsonPropertyName("feeds")] FeedOutcomeDto[] Feeds);

public sealed record NuGetVersionsResultDto(
    [property: JsonPropertyName("versions")] string[] Versions,
    [property: JsonPropertyName("feeds")] FeedOutcomeDto[] Feeds);

public sealed record NuGetIconDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("dataUri")] string? DataUri);

public sealed record PackageDependencyDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("versionRange")] string VersionRange);

public sealed record PackageDependencyGroupDto(
    [property: JsonPropertyName("targetFramework")] string TargetFramework,
    [property: JsonPropertyName("dependencies")] PackageDependencyDto[] Dependencies);

public sealed record PackageDeprecationDto(
    [property: JsonPropertyName("reasons")] string[] Reasons,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("alternatePackageId")] string? AlternatePackageId,
    [property: JsonPropertyName("alternateVersionRange")] string? AlternateVersionRange);

public sealed record PackageVulnerabilityDto(
    [property: JsonPropertyName("severity")] int Severity,
    [property: JsonPropertyName("advisoryUrl")] string? AdvisoryUrl);

public sealed record PackageMetadataDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("authors")] string? Authors,
    [property: JsonPropertyName("owners")] string? Owners,
    [property: JsonPropertyName("tags")] string? Tags,
    [property: JsonPropertyName("downloads")] long? Downloads,
    [property: JsonPropertyName("published")] string? Published,
    [property: JsonPropertyName("isListed")] bool IsListed,
    [property: JsonPropertyName("prefixReserved")] bool PrefixReserved,
    [property: JsonPropertyName("requireLicenseAcceptance")] bool RequireLicenseAcceptance,
    [property: JsonPropertyName("licenseExpression")] string? LicenseExpression,
    [property: JsonPropertyName("licenseFileText")] string? LicenseFileText,
    [property: JsonPropertyName("licenseUrl")] string? LicenseUrl,
    [property: JsonPropertyName("projectUrl")] string? ProjectUrl,
    [property: JsonPropertyName("packageDetailsUrl")] string? PackageDetailsUrl,
    [property: JsonPropertyName("reportAbuseUrl")] string? ReportAbuseUrl,
    [property: JsonPropertyName("iconUrl")] string? IconUrl,
    [property: JsonPropertyName("readmeMarkdown")] string? ReadmeMarkdown,
    [property: JsonPropertyName("dependencyGroups")] PackageDependencyGroupDto[] DependencyGroups,
    [property: JsonPropertyName("deprecation")] PackageDeprecationDto? Deprecation,
    [property: JsonPropertyName("vulnerabilities")] PackageVulnerabilityDto[] Vulnerabilities,
    [property: JsonPropertyName("allVersions")] string[] AllVersions,
    [property: JsonPropertyName("sourceName")] string? SourceName);

public sealed record NuGetFrameworkCheckDto(
    [property: JsonPropertyName("compatible")] bool Compatible,
    [property: JsonPropertyName("unsupported")] FrameworkMismatchDto[] Unsupported,
    [property: JsonPropertyName("packageFrameworks")] string[] PackageFrameworks,
    [property: JsonPropertyName("warning")] string? Warning);

public sealed record FrameworkMismatchDto(
    [property: JsonPropertyName("projectPath")] string ProjectPath,
    [property: JsonPropertyName("projectName")] string ProjectName,
    [property: JsonPropertyName("targetFrameworks")] string[] TargetFrameworks);

/// <param name="LatestUncapped">The newest usable version beyond the platform band, when band
/// alignment held <paramref name="LatestVersion"/> back. Disclosure only.</param>
public sealed record PackageUpdateDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("currentVersion")] string CurrentVersion,
    [property: JsonPropertyName("latestVersion")] string LatestVersion,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("projectPath")] string ProjectPath,
    [property: JsonPropertyName("projectName")] string ProjectName,
    [property: JsonPropertyName("isCentrallyManaged")] bool IsCentrallyManaged,
    [property: JsonPropertyName("isGlobalPackageReference")] bool IsGlobalPackageReference,
    [property: JsonPropertyName("versionSource")] string? VersionSource,
    [property: JsonPropertyName("latestUncapped")] string? LatestUncapped = null);

public sealed record NuGetUpdatesResultDto(
    [property: JsonPropertyName("updates")] PackageUpdateDto[] Updates,
    [property: JsonPropertyName("feeds")] FeedOutcomeDto[] Feeds);

public sealed record NuGetUpdateOutcomeDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("projectPath")] string ProjectPath,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] string? Message);

/// <param name="RequiredBy">The selected package whose new version asks for this one.</param>
public sealed record InducedUpdateDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("currentVersion")] string CurrentVersion,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("projectPath")] string ProjectPath,
    [property: JsonPropertyName("projectName")] string ProjectName,
    [property: JsonPropertyName("requiredBy")] string RequiredBy,
    [property: JsonPropertyName("requiredByVersion")] string RequiredByVersion);

public sealed record NuGetUpdatePlanResultDto(
    [property: JsonPropertyName("induced")] InducedUpdateDto[] Induced);

public sealed record NuGetUpdateAllResultDto(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("results")] NuGetUpdateOutcomeDto[] Results);

public sealed record TransitivePackageDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("targetFramework")] string TargetFramework,
    [property: JsonPropertyName("hasChildren")] bool HasChildren);

public sealed record NuGetTransitiveDto(
    [property: JsonPropertyName("projectPath")] string ProjectPath,
    [property: JsonPropertyName("targetFrameworks")] string[] TargetFrameworks,
    [property: JsonPropertyName("packages")] TransitivePackageDto[] Packages);

public sealed record PackageAdvisoryDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("projectPath")] string ProjectPath,
    [property: JsonPropertyName("targetFramework")] string TargetFramework,
    [property: JsonPropertyName("isTransitive")] bool IsTransitive,
    [property: JsonPropertyName("severity")] int Severity,
    [property: JsonPropertyName("advisoryUrl")] string? AdvisoryUrl);

public sealed record PackageDeprecationEntryDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("projectPath")] string ProjectPath,
    [property: JsonPropertyName("targetFramework")] string TargetFramework,
    [property: JsonPropertyName("isTransitive")] bool IsTransitive,
    [property: JsonPropertyName("reasons")] string[] Reasons,
    [property: JsonPropertyName("alternatePackageId")] string? AlternatePackageId,
    [property: JsonPropertyName("alternateVersionRange")] string? AlternateVersionRange);

public sealed record NuGetAuditDto(
    [property: JsonPropertyName("vulnerabilities")] PackageAdvisoryDto[] Vulnerabilities,
    [property: JsonPropertyName("deprecations")] PackageDeprecationEntryDto[] Deprecations,
    [property: JsonPropertyName("error")] string? Error);

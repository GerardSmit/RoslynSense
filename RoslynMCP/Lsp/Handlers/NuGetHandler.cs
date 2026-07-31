using System.Text.Json.Serialization;
using RoslynMCP.Services.Packages;

namespace RoslynMCP.Lsp.Handlers;

public sealed record NuGetSearchParams(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("includePrerelease")] bool IncludePrerelease = false,
    [property: JsonPropertyName("skip")] int Skip = 0,
    [property: JsonPropertyName("take")] int Take = 30);

public sealed record NuGetVersionsParams(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("includePrerelease")] bool IncludePrerelease = false);

public sealed record NuGetUpdatesParams(
    [property: JsonPropertyName("includePrerelease")] bool IncludePrerelease = false);

public sealed record NuGetOperationParams(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string? Version = null,
    [property: JsonPropertyName("projectPaths")] string[]? ProjectPaths = null);

public sealed record PackageSummaryDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("authors")] string? Authors,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("downloads")] long? Downloads,
    [property: JsonPropertyName("iconDataUri")] string? IconDataUri,
    [property: JsonPropertyName("deprecated")] bool Deprecated,
    [property: JsonPropertyName("vulnerable")] bool Vulnerable,
    [property: JsonPropertyName("installedVersion")] string? InstalledVersion);

public sealed record ProjectPackagesDto(
    [property: JsonPropertyName("projectPath")] string ProjectPath,
    [property: JsonPropertyName("projectName")] string ProjectName,
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

/// <summary>
/// The package panel's server side. Icons are converted to data URIs here because the webview
/// runs under a CSP that forbids remote images outright — the alternative is no icons at all.
/// </summary>
internal static class NuGetHandler
{
    public static async Task<PackageSummaryDto[]> SearchAsync(NuGetSearchParams p, CancellationToken ct)
    {
        var results = await NuGetService.SearchAsync(p.Query, p.IncludePrerelease, p.Skip, p.Take, ct);
        return await ToDtosAsync(results, ct);
    }

    public static Task<IReadOnlyList<string>> VersionsAsync(NuGetVersionsParams p, CancellationToken ct) =>
        NuGetService.VersionsAsync(p.Id, p.IncludePrerelease, ct)
            .ContinueWith(t => t.Result, ct);

    public static async Task<ProjectPackagesDto[]> InstalledAsync(CancellationToken ct)
    {
        var projects = await NuGetService.InstalledAsync(ct);
        return projects
            .Select(project => new ProjectPackagesDto(
                project.ProjectPath,
                project.ProjectName,
                project.Packages.Select(ToDto).ToArray()))
            .ToArray();
    }

    public static async Task<PackageSummaryDto[]> UpdatesAsync(NuGetUpdatesParams p, CancellationToken ct)
    {
        var updates = await NuGetService.UpdatesAsync(p.IncludePrerelease, ct);
        return updates.Select(ToDto).ToArray();
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

    public static async Task<PackageOperationDto> InstallAsync(NuGetOperationParams p, CancellationToken ct)
    {
        var result = await NuGetService.InstallAsync(p.Id, p.Version, p.ProjectPaths ?? [], ct);
        await AfterMutationAsync(ct);
        return new PackageOperationDto(result.Success, result.Message);
    }

    public static async Task<PackageOperationDto> UninstallAsync(NuGetOperationParams p, CancellationToken ct)
    {
        var result = await NuGetService.UninstallAsync(p.Id, p.ProjectPaths ?? [], ct);
        await AfterMutationAsync(ct);
        return new PackageOperationDto(result.Success, result.Message);
    }

    public static async Task<PackageOperationDto> ConsolidateAsync(NuGetOperationParams p, CancellationToken ct)
    {
        var result = await NuGetService.ConsolidateAsync(p.Id, p.Version ?? "", ct);
        await AfterMutationAsync(ct);
        return new PackageOperationDto(result.Success, result.Message);
    }

    public static string[] Sources() => NuGetService.Sources().ToArray();

    /// <summary>The package set changed, so squiggles, lenses and hints are all stale.</summary>
    private static Task AfterMutationAsync(CancellationToken ct)
    {
        AnalyzerDiagnosticCache.Clear();
        return LspSessionRegistry.RequestRefreshAsync(RefreshKind.All, ct);
    }

    private static async Task<PackageSummaryDto[]> ToDtosAsync(
        IReadOnlyList<PackageSummary> packages, CancellationToken ct)
    {
        var icons = await Task.WhenAll(packages.Select(async package =>
            package.IconUrl is null ? null : await NuGetService.IconDataUriAsync(package.IconUrl, ct)));

        return packages
            .Select((package, index) => ToDto(package) with { IconDataUri = icons[index] })
            .ToArray();
    }

    private static PackageSummaryDto ToDto(PackageSummary package) =>
        new(package.Id, package.Version, package.Authors, package.Description, package.Downloads,
            IconDataUri: null, package.Deprecated, package.Vulnerable, package.InstalledVersion);
}

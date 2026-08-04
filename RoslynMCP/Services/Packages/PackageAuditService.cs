using System.Text.Json;

namespace RoslynMCP.Services.Packages;

/// <param name="Severity">NuGet's own scale: 0 low, 1 moderate, 2 high, 3 critical.</param>
public sealed record PackageAdvisory(
    string Id,
    string Version,
    string ProjectPath,
    string TargetFramework,
    bool IsTransitive,
    int Severity,
    string? AdvisoryUrl);

public sealed record PackageDeprecation(
    string Id,
    string Version,
    string ProjectPath,
    string TargetFramework,
    bool IsTransitive,
    IReadOnlyList<string> Reasons,
    string? AlternatePackageId,
    string? AlternateVersionRange);

public sealed record PackageAudit(
    IReadOnlyList<PackageAdvisory> Vulnerabilities,
    IReadOnlyList<PackageDeprecation> Deprecations,
    string? Error);

/// <summary>
/// Known vulnerabilities and deprecations across the whole solution.
/// </summary>
/// <remarks>
/// Deliberately the CLI rather than a per-package feed query: <c>dotnet list package</c> answers
/// for every project, every target framework and — crucially — every <em>transitive</em> package
/// in two processes, where asking the registration index would mean one HTTP request per installed
/// package just to paint a list. Transitive advisories are the ones that matter most and the ones
/// a per-package lookup would miss entirely, since nothing in the project file mentions them.
///
/// Per-package detail for the package the user actually clicked still comes from
/// <see cref="NuGetMetadataService"/>.
/// </remarks>
public static class PackageAuditService
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private static readonly SemaphoreSlim s_gate = new(1, 1);

    /// <summary>
    /// A reference rather than a nullable tuple: the fast path reads this outside the lock, and a
    /// multi-word struct is not written atomically — a reader could see a set timestamp beside a
    /// null result and hand back null from a method that promises not to.
    /// </summary>
    private sealed record CachedAudit(DateTime FetchedUtc, PackageAudit Audit);

    private static volatile CachedAudit? s_cached;

    public static async Task<PackageAudit> AuditAsync(bool refresh, CancellationToken ct)
    {
        if (!refresh && s_cached is { } cached && DateTime.UtcNow - cached.FetchedUtc < Lifetime)
            return cached.Audit;

        await s_gate.WaitAsync(ct);
        try
        {
            if (!refresh && s_cached is { } current && DateTime.UtcNow - current.FetchedUtc < Lifetime)
                return current.Audit;

            var audit = await RunAsync(ct);
            s_cached = new CachedAudit(DateTime.UtcNow, audit);
            return audit;
        }
        finally
        {
            s_gate.Release();
        }
    }

    public static void Invalidate() => s_cached = null;

    private static async Task<PackageAudit> RunAsync(CancellationToken ct)
    {
        string? target = WorkspaceService.TryGetMostRecentSolution()?.FilePath;
        if (target is not { Length: > 0 })
            return new PackageAudit([], [], "No solution is loaded.");

        await using var progress = await ProgressReporter.BeginAsync("Auditing packages", ct);

        // The two switches are mutually exclusive on the CLI, so this is two runs, not one.
        var vulnerable = await ListAsync(target, "--vulnerable", ct);
        progress.Report("deprecated packages", 50);
        var deprecated = await ListAsync(target, "--deprecated", ct);

        var errors = new[] { vulnerable.Error, deprecated.Error }
            .Where(e => e is { Length: > 0 })
            .ToList();

        return new PackageAudit(
            ParseVulnerabilities(vulnerable.Document),
            ParseDeprecations(deprecated.Document),
            errors.Count == 0 ? null : string.Join("; ", errors));
    }

    private static async Task<(JsonDocument? Document, string? Error)> ListAsync(
        string target, string switchName, CancellationToken ct)
    {
        var (exitCode, output) = await NuGetService.RunDotnetAsync(
            ["list", target, "package", switchName, "--include-transitive", "--format", "json"], ct);

        // A solution that has never restored has no assets file, which the CLI reports rather than
        // crashing. Say so, instead of reporting a clean bill of health.
        if (exitCode != 0)
            return (null, NuGetService.FirstLine(output));

        try
        {
            int start = output.IndexOf('{');
            return start < 0
                ? (null, null)
                : (JsonDocument.Parse(output[start..]), null);
        }
        catch (JsonException ex)
        {
            return (null, $"Could not read the audit output: {ex.Message}");
        }
    }

    private static IReadOnlyList<PackageAdvisory> ParseVulnerabilities(JsonDocument? document)
    {
        var advisories = new List<PackageAdvisory>();

        foreach (var (projectPath, framework, package, transitive) in Packages(document))
        {
            if (!package.TryGetProperty("vulnerabilities", out var vulnerabilities) ||
                vulnerabilities.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var vulnerability in vulnerabilities.EnumerateArray())
            {
                advisories.Add(new PackageAdvisory(
                    Text(package, "id") ?? "",
                    Text(package, "resolvedVersion") ?? Text(package, "requestedVersion") ?? "",
                    projectPath,
                    framework,
                    transitive,
                    SeverityOf(Text(vulnerability, "severity")),
                    Text(vulnerability, "advisoryurl") ?? Text(vulnerability, "advisoryUrl")));
            }
        }

        return advisories;
    }

    private static IReadOnlyList<PackageDeprecation> ParseDeprecations(JsonDocument? document)
    {
        var deprecations = new List<PackageDeprecation>();

        foreach (var (projectPath, framework, package, transitive) in Packages(document))
        {
            if (!package.TryGetProperty("deprecationReasons", out var reasons) ||
                reasons.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            JsonElement? alternate = package.TryGetProperty("alternativePackage", out var value)
                ? value
                : null;

            deprecations.Add(new PackageDeprecation(
                Text(package, "id") ?? "",
                Text(package, "resolvedVersion") ?? Text(package, "requestedVersion") ?? "",
                projectPath,
                framework,
                transitive,
                reasons.EnumerateArray().Select(r => r.GetString() ?? "").Where(r => r.Length > 0).ToList(),
                alternate is { } a ? Text(a, "id") : null,
                alternate is { } b ? Text(b, "versionRange") : null));
        }

        return deprecations;
    }

    /// <summary>Flattens the CLI's project → framework → package nesting.</summary>
    private static IEnumerable<(string ProjectPath, string Framework, JsonElement Package, bool Transitive)> Packages(
        JsonDocument? document)
    {
        if (document is null || !document.RootElement.TryGetProperty("projects", out var projects))
            yield break;

        foreach (var project in projects.EnumerateArray())
        {
            string projectPath = Text(project, "path") ?? "";
            if (!project.TryGetProperty("frameworks", out var frameworks) ||
                frameworks.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var framework in frameworks.EnumerateArray())
            {
                string moniker = Text(framework, "framework") ?? "";

                foreach (var (property, transitive) in
                         new[] { ("topLevelPackages", false), ("transitivePackages", true) })
                {
                    if (!framework.TryGetProperty(property, out var packages) ||
                        packages.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var package in packages.EnumerateArray())
                        yield return (projectPath, moniker, package, transitive);
                }
            }
        }
    }

    /// <summary>The CLI prints a word; the panel and the AI both want it ordered.</summary>
    private static int SeverityOf(string? severity) => severity?.ToLowerInvariant() switch
    {
        "critical" => 3,
        "high" => 2,
        "moderate" or "medium" => 1,
        _ => 0,
    };

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

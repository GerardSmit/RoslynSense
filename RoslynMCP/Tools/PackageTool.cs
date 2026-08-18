using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using RoslynMCP.Services;
using RoslynMCP.Services.Packages;

namespace RoslynMCP.Tools;

/// <summary>
/// Package management for the AI. Without these, adding a package means shelling
/// `dotnet add package` through Bash, which edits the project behind the loaded workspace's
/// back and leaves every later analysis answering from a stale snapshot.
/// </summary>
[McpServerToolType]
public static class PackageTool
{
    [McpServerTool, Description(
        "NuGet packages in the solution. view: installed (default) lists direct references with " +
        "versions; outdated lists packages with a newer version available per project — the dry " +
        "run for UpdatePackages; audit lists known vulnerabilities and deprecations, including " +
        "transitive packages.")]
    public static async Task<string> ListPackages(
        IOutputFormatter fmt,
        [Description("View: installed (default), outdated, or audit.")]
        string view = "installed",
        [Description("Optional project path. Omit for every project in the solution. Ignored for view=audit.")]
        string? projectPath = null,
        [Description("view=outdated: include prerelease versions (default: false).")]
        bool includePrerelease = false,
        [Description("view=outdated: how far a version may move: none, major (stay on the current major), minor.")]
        string versionLock = "none",
        [Description("view=outdated: keep platform-tracking packages (Microsoft.Extensions.*, System.*, ...) on " +
            "the .NET major the project targets (default: true).")]
        bool alignPlatform = true,
        [Description("view=audit: re-run the audit instead of reusing a recent result.")]
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        switch (view.ToLowerInvariant())
        {
            case "installed":
                break;
            case "outdated":
                return await ListOutdatedAsync(
                    fmt, projectPath, includePrerelease, versionLock, alignPlatform, cancellationToken);
            case "audit":
                return await AuditAsync(fmt, refresh, cancellationToken);
            default:
                return $"Error: Unknown view '{view}'. Use: installed, outdated, audit.";
        }

        var projects = await NuGetService.InstalledAsync(cancellationToken);
        if (projectPath is { Length: > 0 })
        {
            string normalized = PathHelper.NormalizePath(projectPath);
            projects = projects
                .Where(p => string.Equals(p.ProjectPath, normalized, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (projects.Count == 0)
            return "No projects with package references were found.";

        var sb = new StringBuilder();
        fmt.AppendHeader(sb, "Packages");
        foreach (var project in projects)
        {
            fmt.AppendHeader(sb, project.ProjectName, 2);
            if (project.Packages.Count == 0)
            {
                sb.AppendLine("_No direct package references._");
                continue;
            }

            fmt.BeginTable(sb, project.ProjectName, ["Package", "Version", "Managed in"], project.Packages.Count);
            foreach (var package in project.Packages.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
            {
                fmt.BeginRow(sb);
                fmt.WriteCell(sb, package.Id);
                fmt.WriteCell(sb, package.Version.Length > 0 ? package.Version : "—");
                // Under Central Package Management the csproj carries no version, so the file to
                // edit is the answer that actually matters.
                fmt.WriteCell(sb, package.IsCentrallyManaged && package.VersionSource is { } source
                    ? Path.GetFileName(source)
                    : "project");
                fmt.EndRow(sb);
            }
            fmt.EndTable(sb);
        }
        return sb.ToString();
    }

    [McpServerTool, Description(
        "Search configured NuGet feeds for packages matching a query.")]
    public static async Task<string> SearchPackages(
        [Description("Search terms.")] string query,
        IOutputFormatter fmt,
        [Description("Include prerelease versions (default: false).")] bool includePrerelease = false,
        [Description("Maximum results (default: 20).")] int maxResults = 20,
        CancellationToken cancellationToken = default)
    {
        var found = await NuGetService.SearchAsync(
            query, includePrerelease, 0, Math.Clamp(maxResults, 1, 100), source: null, cancellationToken);

        if (found.Results.Count == 0)
            return $"No packages matched '{query}'.{FeedProblems(found.Feeds)}";

        var sb = new StringBuilder();
        fmt.AppendHeader(sb, $"Packages matching '{query}'");
        fmt.BeginTable(sb, "results", ["Package", "Latest", "Installed", "Feed", "Description"], found.Results.Count);
        foreach (var package in found.Results)
        {
            fmt.BeginRow(sb);
            fmt.WriteCell(sb, package.Id);
            fmt.WriteCell(sb, package.Version);
            fmt.WriteCell(sb, package.InstalledVersion ?? "—");
            fmt.WriteCell(sb, package.SourceName ?? "—");
            fmt.WriteCell(sb, Truncate(package.Description ?? "", 100));
            fmt.EndRow(sb);
        }
        fmt.EndTable(sb);
        sb.Append(FeedProblems(found.Feeds));
        return sb.ToString();
    }

    [McpServerTool, Description(
        "Add a NuGet package to one or more projects. Honors NuGet.config sources and Central " +
        "Package Management, and reloads the workspace so later analysis sees the change.")]
    public static async Task<string> AddPackage(
        [Description("Package id.")] string packageId,
        [Description("Project path(s), semicolon-separated.")] string projectPath,
        [Description("Version to install. Omit for the latest stable.")] string? version = null,
        CancellationToken cancellationToken = default)
    {
        var projects = SplitProjects(projectPath);
        if (projects.Count == 0)
            return "Error: at least one project path is required.";

        string warning = version is { Length: > 0 }
            ? await FrameworkWarningAsync(packageId, version, projects, cancellationToken)
            : "";

        var result = await NuGetService.InstallAsync(packageId, version, projects, cancellationToken);
        return warning.Length > 0 ? $"{result.Message}\n\n{warning}" : result.Message;
    }

    [McpServerTool, Description("Remove a NuGet package from one or more projects.")]
    public static async Task<string> RemovePackage(
        [Description("Package id.")] string packageId,
        [Description("Project path(s), semicolon-separated.")] string projectPath,
        CancellationToken cancellationToken = default)
    {
        var projects = SplitProjects(projectPath);
        if (projects.Count == 0)
            return "Error: at least one project path is required.";

        var result = await NuGetService.UninstallAsync(packageId, projects, cancellationToken);
        return result.Message;
    }

    [McpServerTool, Description(
        "Align every project that references a package onto one version.")]
    public static async Task<string> ConsolidatePackage(
        [Description("Package id.")] string packageId,
        [Description("Version every project should use.")] string version,
        CancellationToken cancellationToken = default)
    {
        var result = await NuGetService.ConsolidateAsync(packageId, version, cancellationToken);
        return result.Message;
    }

    [McpServerTool, Description(
        "Packages referenced at more than one version across the solution.")]
    public static async Task<string> FindPackageConflicts(
        IOutputFormatter fmt, CancellationToken cancellationToken = default)
    {
        var conflicts = await NuGetService.ConsolidationsAsync(cancellationToken);
        if (conflicts.Count == 0)
            return "Every package is referenced at a single version.";

        var sb = new StringBuilder();
        fmt.AppendHeader(sb, "Version conflicts");
        foreach (var conflict in conflicts)
        {
            fmt.AppendHeader(sb, conflict.Id, 2);
            foreach (var use in conflict.Versions.OrderBy(v => v.ProjectName, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"- {use.ProjectName}: {use.Version}");
        }
        return sb.ToString();
    }

    private static async Task<string> ListOutdatedAsync(
        IOutputFormatter fmt,
        string? projectPath,
        bool includePrerelease,
        string versionLock,
        bool alignPlatform,
        CancellationToken cancellationToken)
    {
        var found = await PackageUpdateService.OutdatedAsync(
            BuildQuery(projectPath, includePrerelease, versionLock, refresh: false, alignPlatform),
            cancellationToken);

        if (found.Results.Count == 0)
            return $"Every package is up to date.{FeedProblems(found.Feeds)}";

        var sb = new StringBuilder();
        fmt.AppendHeader(sb, "Outdated packages");
        fmt.BeginTable(sb, "outdated", ["Package", "Project", "Current", "Latest", "Change"], found.Results.Count);
        foreach (var update in found.Results)
        {
            fmt.BeginRow(sb);
            fmt.WriteCell(sb, update.Id);
            fmt.WriteCell(sb, update.ProjectName);
            fmt.WriteCell(sb, update.CurrentVersion);
            fmt.WriteCell(sb, update.LatestVersion);
            fmt.WriteCell(sb, update.Severity.ToString().ToLowerInvariant());
            fmt.EndRow(sb);
        }
        fmt.EndTable(sb);

        var capped = found.Results.Where(u => u.LatestUncapped is not null).ToList();
        if (capped.Count > 0)
        {
            sb.AppendLine(
                "Band-aligned to the project's .NET major: " +
                string.Join(", ", capped.Select(u => $"{u.Id} ({u.LatestUncapped} exists)").Distinct()) +
                ". Pass alignPlatform: false to offer the newer band.");
        }

        sb.Append(FeedProblems(found.Feeds));
        return sb.ToString();
    }

    [McpServerTool, Description(
        "Update packages to their latest allowed version in one pass, then restore once. " +
        "Writes Directory.Packages.props directly where Central Package Management is in use.")]
    public static async Task<string> UpdatePackages(
        IOutputFormatter fmt,
        [Description("Package id(s), semicolon-separated. Omit to update everything outdated.")]
        string? packageIds = null,
        [Description("Optional project path(s), semicolon-separated. Omit for the whole solution.")]
        string? projectPath = null,
        [Description("How far a version may move: none, major (stay on the current major), minor.")]
        string versionLock = "none",
        [Description("Include prerelease versions (default: false).")] bool includePrerelease = false,
        [Description("Keep platform-tracking packages (Microsoft.Extensions.*, System.*, ...) on " +
            "the .NET major the project targets (default: true).")]
        bool alignPlatform = true,
        CancellationToken cancellationToken = default)
    {
        var found = await PackageUpdateService.OutdatedAsync(
            BuildQuery(projectPath, includePrerelease, versionLock, refresh: true, alignPlatform),
            cancellationToken);

        var wanted = packageIds is { Length: > 0 }
            ? packageIds.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        var requests = found.Results
            .Where(u => u.Severity != UpdateSeverity.Unknown)
            .Where(u => wanted is null || wanted.Contains(u.Id))
            .GroupBy(u => (u.Id, u.LatestVersion))
            .Select(g => new PackageUpdateRequest(
                g.Key.Id, g.Key.LatestVersion, g.Select(u => u.ProjectPath).Distinct().ToList()))
            .ToList();

        if (requests.Count == 0)
            return $"Nothing to update.{FeedProblems(found.Feeds)}";

        var result = await PackageUpdateService.UpdateAllAsync(requests, restore: true, cancellationToken);

        var sb = new StringBuilder();
        sb.AppendLine(result.Message);

        var failures = result.Results.Where(r => !r.Success).ToList();
        if (failures.Count > 0)
        {
            fmt.AppendHeader(sb, "Failures", 2);
            foreach (var failure in failures)
                sb.AppendLine($"- {failure.Id} {failure.Version} in {Path.GetFileName(failure.ProjectPath)}: {failure.Message}");
        }

        return sb.ToString();
    }

    private static async Task<string> AuditAsync(
        IOutputFormatter fmt,
        bool refresh,
        CancellationToken cancellationToken)
    {
        var audit = await PackageAuditService.AuditAsync(refresh, cancellationToken);

        if (audit.Error is { Length: > 0 } && audit.Vulnerabilities.Count == 0 && audit.Deprecations.Count == 0)
            return $"Could not audit packages: {audit.Error}";

        if (audit.Vulnerabilities.Count == 0 && audit.Deprecations.Count == 0)
            return "No known vulnerabilities or deprecations.";

        var sb = new StringBuilder();

        if (audit.Vulnerabilities.Count > 0)
        {
            fmt.AppendHeader(sb, "Vulnerabilities");
            fmt.BeginTable(sb, "vulnerabilities",
                ["Package", "Version", "Severity", "Project", "Direct", "Advisory"],
                audit.Vulnerabilities.Count);
            foreach (var advisory in audit.Vulnerabilities.OrderByDescending(v => v.Severity))
            {
                fmt.BeginRow(sb);
                fmt.WriteCell(sb, advisory.Id);
                fmt.WriteCell(sb, advisory.Version);
                fmt.WriteCell(sb, SeverityName(advisory.Severity));
                fmt.WriteCell(sb, Path.GetFileNameWithoutExtension(advisory.ProjectPath));
                fmt.WriteCell(sb, advisory.IsTransitive ? "transitive" : "direct");
                fmt.WriteCell(sb, advisory.AdvisoryUrl ?? "—");
                fmt.EndRow(sb);
            }
            fmt.EndTable(sb);
        }

        if (audit.Deprecations.Count > 0)
        {
            fmt.AppendHeader(sb, "Deprecated");
            foreach (var deprecation in audit.Deprecations)
            {
                string alternate = deprecation.AlternatePackageId is { Length: > 0 } id
                    ? $" — use {id} {deprecation.AlternateVersionRange}".TrimEnd()
                    : "";
                sb.AppendLine(
                    $"- {deprecation.Id} {deprecation.Version} in " +
                    $"{Path.GetFileNameWithoutExtension(deprecation.ProjectPath)} " +
                    $"({string.Join(", ", deprecation.Reasons)}){alternate}");
            }
        }

        if (audit.Error is { Length: > 0 })
            sb.AppendLine($"\n_Partial result: {audit.Error}_");

        return sb.ToString();
    }

    [McpServerTool, Description(
        "List the configured NuGet feeds, including disabled ones, and which NuGet.config declares each.")]
    public static string ListPackageSources(IOutputFormatter fmt)
    {
        var sources = NuGetService.Sources();
        if (sources.Count == 0)
            return "No NuGet feeds are configured.";

        var sb = new StringBuilder();
        fmt.AppendHeader(sb, "Package sources");
        fmt.BeginTable(sb, "sources", ["Order", "Name", "URL", "State", "Declared in"], sources.Count);

        int order = 0;
        foreach (var source in sources)
        {
            fmt.BeginRow(sb);
            // Order decides which feed answers first, so it is worth stating rather than implying.
            fmt.WriteCell(sb, (++order).ToString());
            fmt.WriteCell(sb, source.Name);
            fmt.WriteCell(sb, source.Source);
            fmt.WriteCell(sb, source.IsEnabled ? "enabled" : "disabled");
            fmt.WriteCell(sb, source.ConfigFilePath is { } path ? Path.GetFileName(path) : "—");
            fmt.EndRow(sb);
        }
        fmt.EndTable(sb);
        return sb.ToString();
    }

    [McpServerTool, Description(
        "Add, retarget, rename, remove, enable or disable a NuGet feed in the solution's NuGet.config.")]
    public static string ManagePackageSource(
        [Description("add, update, remove, enable or disable.")] string action,
        [Description("Feed name.")] string name,
        [Description("Feed URL or folder path. Required for add; optional for update.")]
        string? source = null,
        [Description("New name, for a rename.")] string? newName = null)
    {
        var result = action.ToLowerInvariant() switch
        {
            "add" => NuGetFeedContext.AddSource(name, source ?? ""),
            "update" => NuGetFeedContext.UpdateSource(name, newName, source),
            "remove" => NuGetFeedContext.RemoveSource(name),
            "enable" => NuGetFeedContext.SetSourceEnabled(name, true),
            "disable" => NuGetFeedContext.SetSourceEnabled(name, false),
            _ => new PackageOperationResult(false, $"Unknown action '{action}'. Use add, update, remove, enable or disable."),
        };

        if (result.Success)
        {
            PackageUpdateService.Invalidate();
            NuGetMetadataService.Invalidate();
        }

        return result.Message;
    }

    private static UpdateQuery BuildQuery(
        string? projectPath, bool includePrerelease, string versionLock, bool refresh,
        bool alignPlatform = true) =>
        new(IncludePrerelease: includePrerelease,
            Lock: versionLock.ToLowerInvariant() switch
            {
                "major" => VersionLock.Major,
                "minor" => VersionLock.Minor,
                _ => VersionLock.None,
            },
            Prerelease: includePrerelease ? PrereleaseReporting.Always : PrereleaseReporting.Auto,
            ProjectPaths: projectPath is { Length: > 0 } ? SplitProjects(projectPath) : null,
            Refresh: refresh,
            AlignPlatform: alignPlatform);

    /// <summary>
    /// A target-framework mismatch, stated before it becomes an NU1202 at restore time. Advisory
    /// only — packages that ship analyzers or native assets declare no dependency groups at all.
    /// </summary>
    private static async Task<string> FrameworkWarningAsync(
        string packageId, string version, IReadOnlyList<string> projects, CancellationToken ct)
    {
        var mismatches = new List<string>();

        foreach (string project in projects)
        {
            var frameworks = await PackageFrameworkService.FrameworksOfAsync(project, ct);
            if (frameworks.Count == 0)
                continue;

            var check = await PackageFrameworkService.CheckAsync(packageId, version, frameworks, ct);
            if (!check.Compatible)
            {
                mismatches.Add(
                    $"{Path.GetFileNameWithoutExtension(project)} targets " +
                    $"{string.Join(", ", check.UnsupportedFrameworks)}, which {packageId} {version} " +
                    $"does not support (it ships {string.Join(", ", check.PackageFrameworks)})");
            }
        }

        return mismatches.Count == 0 ? "" : "Warning: " + string.Join("; ", mismatches) + ".";
    }

    /// <summary>
    /// Feeds that failed, appended to every result. An answer that silently omits the feed holding
    /// the package reads as "it does not exist", which is the wrong conclusion to hand an agent.
    /// </summary>
    private static string FeedProblems(IReadOnlyList<FeedOutcome> feeds)
    {
        var failed = feeds.Where(f => !f.Ok).ToList();
        if (failed.Count == 0)
            return "";

        var lines = failed.Select(f => f.Unauthorized
            ? $"- {f.Name}: needs credentials"
            : $"- {f.Name}: {f.Error}");

        return $"\n\n_{failed.Count} of {feeds.Count} feeds did not answer:_\n{string.Join("\n", lines)}\n";
    }

    private static string SeverityName(int severity) => severity switch
    {
        3 => "critical",
        2 => "high",
        1 => "moderate",
        _ => "low",
    };

    private static List<string> SplitProjects(string projectPath) =>
        projectPath
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(PathHelper.NormalizePath)
            .ToList();

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}

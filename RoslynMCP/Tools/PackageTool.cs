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
        "List NuGet packages referenced by the solution's projects, with their versions.")]
    public static async Task<string> ListPackages(
        IOutputFormatter fmt,
        [Description("Optional project path. Omit for every project in the solution.")]
        string? projectPath = null,
        CancellationToken cancellationToken = default)
    {
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

            fmt.BeginTable(sb, project.ProjectName, ["Package", "Version"], project.Packages.Count);
            foreach (var package in project.Packages.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
            {
                fmt.BeginRow(sb);
                fmt.WriteCell(sb, package.Id);
                fmt.WriteCell(sb, package.Version);
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
        var results = await NuGetService.SearchAsync(
            query, includePrerelease, 0, Math.Clamp(maxResults, 1, 100), cancellationToken);

        if (results.Count == 0)
            return $"No packages matched '{query}'.";

        var sb = new StringBuilder();
        fmt.AppendHeader(sb, $"Packages matching '{query}'");
        fmt.BeginTable(sb, "results", ["Package", "Latest", "Installed", "Description"], results.Count);
        foreach (var package in results)
        {
            fmt.BeginRow(sb);
            fmt.WriteCell(sb, package.Id);
            fmt.WriteCell(sb, package.Version);
            fmt.WriteCell(sb, package.InstalledVersion ?? "—");
            fmt.WriteCell(sb, Truncate(package.Description ?? "", 100));
            fmt.EndRow(sb);
        }
        fmt.EndTable(sb);
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

        var result = await NuGetService.InstallAsync(packageId, version, projects, cancellationToken);
        return result.Message;
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

    private static List<string> SplitProjects(string projectPath) =>
        projectPath
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(PathHelper.NormalizePath)
            .ToList();

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}

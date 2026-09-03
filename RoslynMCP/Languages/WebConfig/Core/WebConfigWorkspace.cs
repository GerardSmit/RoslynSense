using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services;
using RoslynMCP.Tools;

namespace RoslynMCP.Languages.WebConfig.Core;

/// <summary>
/// One <c>.config</c> file together with everything the workspace knows about it: its entries, the
/// project whose code reads them, and the two indexes of what reads them.
/// </summary>
/// <remarks>
/// <see cref="Project"/> is nullable the way <c>AppSettingsView</c>'s is: the file's own structure
/// needs no project, and only counting references does.
/// </remarks>
internal sealed record WebConfigView(
    WebConfigDocument Document,
    Project? Project,
    ConfigurationManagerUsageIndex Index,
    ImmutableArray<ConfigSettingUsage> MarkupUsages)
{
    public string FilePath => Document.FilePath;

    public SourceText Text => Document.Text;
}

/// <summary>Resolves a <c>.config</c> path to its entries, its project and its usage indexes.</summary>
internal static class WebConfigWorkspace
{
    public static async Task<WebConfigView?> GetAsync(string filePath, CancellationToken ct)
    {
        if (WebConfigDocumentCache.Get(filePath) is not { } document)
            return null;

        var project = await ProjectForAsync(document.FilePath, ct);

        return new WebConfigView(
            document,
            project,
            project is null
                ? ConfigurationManagerUsageIndex.Empty
                : await ConfigurationManagerUsageIndex.GetAsync(project, ct),
            project is null
                ? []
                : await MarkupSettingUsageIndex.ForProjectAsync(project, ct));
    }

    /// <summary>
    /// The project whose application reads this file: the nearest one above it, which for a
    /// nested <c>web.config</c> in a subdirectory is the application's own project.
    /// </summary>
    public static async Task<Project?> ProjectForAsync(string filePath, CancellationToken ct)
    {
        if (await NonCSharpProjectFinder.FindProjectAsync(filePath, ct) is not { Length: > 0 } projectPath)
            return null;

        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
            projectPath, targetFilePath: filePath, cancellationToken: ct);

        return project;
    }
}

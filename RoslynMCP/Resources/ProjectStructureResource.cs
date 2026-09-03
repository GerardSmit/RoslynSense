using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Resources;

/// <summary>
/// Exposes the document structure of a .NET project as an MCP resource template.
/// Clients can attach this as context to understand which files a project contains
/// and how they are organized, without reading every file.
/// </summary>
[McpServerResourceType]
public static class ProjectStructureResource
{
    /// <summary>
    /// Returns a compact listing of all source documents in the project that contains
    /// the specified file, grouped by directory with relative paths.
    /// </summary>
    [McpServerResource(
        Name = "project-structure",
        UriTemplate = "roslyn://project-structure/{filePath}",
        MimeType = "text/plain"),
     Description(
        "Get the file/folder structure of the .NET project containing the given file. " +
        "Returns a tree of source documents grouped by directory.")]
    public static async Task<string> GetProjectStructureAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return "Error: filePath cannot be empty.";

        string systemPath = PathHelper.NormalizePath(filePath);

        if (!File.Exists(systemPath))
            return $"Error: File {systemPath} does not exist.";

        string? projectPath = await WorkspaceService.FindContainingProjectAsync(
            systemPath, cancellationToken);

        if (string.IsNullOrEmpty(projectPath))
            return "Error: Couldn't find a project containing this file.";

        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
            projectPath, cancellationToken: cancellationToken);

        string projectDir = Path.GetDirectoryName(projectPath) ?? projectPath;

        var documents = project.Documents
            .Where(d => !string.IsNullOrEmpty(d.FilePath))
            .Select(d => Path.GetRelativePath(projectDir, d.FilePath!))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"**Project: {project.Name}**");
        sb.AppendLine($"**Path**: {projectPath}");
        sb.AppendLine($"**Documents**: {documents.Count}");
        sb.AppendLine();

        // Group by top-level directory for a tree-like view
        var grouped = documents
            .GroupBy(d => Path.GetDirectoryName(d) ?? ".")
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in grouped)
        {
            string folder = string.IsNullOrEmpty(group.Key) ? "." : group.Key;
            sb.AppendLine($"📁 {folder}/");

            foreach (string doc in group.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"  📄 {Path.GetFileName(doc)}");
            }
        }

        return sb.ToString();
    }
}

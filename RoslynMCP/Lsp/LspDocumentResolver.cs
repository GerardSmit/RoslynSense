using Microsoft.CodeAnalysis;
using RoslynMCP.Services;

namespace RoslynMCP.Lsp;

/// <summary>Resolves an LSP document URI to a Roslyn <see cref="Document"/> via the shared
/// <see cref="WorkspaceService"/> — the same path MCP tools take, so both see identical
/// snapshots (including open-buffer overlays).</summary>
internal static class LspDocumentResolver
{
    public static async Task<Document?> ResolveAsync(string filePath, CancellationToken ct)
    {
        string path = PathHelper.NormalizePath(filePath);
        string? projectPath = await WorkspaceService.FindContainingProjectAsync(path, ct);
        if (string.IsNullOrEmpty(projectPath))
            return null;

        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
            projectPath, targetFilePath: path, cancellationToken: ct);

        return WorkspaceService.FindDocumentInProject(project, path);
    }
}

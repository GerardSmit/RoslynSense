using RoslynMCP.Services;

namespace RoslynMCP.Tools;

/// <summary>
/// Locates the containing .csproj project for non-C# files (ASPX, Razor, etc.)
/// by first checking the workspace, then walking parent directories.
/// </summary>
internal static class NonCSharpProjectFinder
{
    public static async Task<string?> FindProjectAsync(
        string filePath, CancellationToken cancellationToken)
    {
        string? projectPath = await WorkspaceService.FindContainingProjectAsync(filePath, cancellationToken);
        if (!string.IsNullOrEmpty(projectPath))
            return projectPath;

        var dir = new DirectoryInfo(Path.GetDirectoryName(filePath)!);
        while (dir is not null)
        {
            var csproj = dir.GetFiles("*.csproj").FirstOrDefault();
            if (csproj is not null)
                return csproj.FullName;
            dir = dir.Parent;
        }

        return null;
    }
}

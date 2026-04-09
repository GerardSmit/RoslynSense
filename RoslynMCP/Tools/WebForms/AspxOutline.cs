using Microsoft.CodeAnalysis;
using RoslynMCP.Services;

namespace RoslynMCP.Tools.WebForms;

/// <summary>
/// Produces a structured outline for ASPX/ASCX files by parsing server controls,
/// directives, and code blocks via WebFormsCore.
/// </summary>
internal class AspxOutline : IOutlineHandler
{
    public bool CanHandle(string filePath) => AspxSourceMappingService.IsAspxFile(filePath);

    public async Task<string> GetOutlineAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
            return $"Error: File {filePath} does not exist.";

        string? projectPath = await NonCSharpProjectFinder.FindProjectAsync(filePath, cancellationToken);

        if (string.IsNullOrEmpty(projectPath))
            return "Error: Couldn't find a project containing this ASPX file.";

        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
            projectPath, cancellationToken: cancellationToken);
        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation is null)
            return "Error: Unable to get compilation for the project.";

        string text = await File.ReadAllTextAsync(filePath, cancellationToken);
        string? projectDir = Path.GetDirectoryName(projectPath);

        var webConfigNamespaces = projectDir is not null
            ? AspxSourceMappingService.LoadWebConfigNamespaces(projectDir)
            : default;

        var result = AspxSourceMappingService.Parse(filePath, text, compilation,
            namespaces: webConfigNamespaces.IsDefaultOrEmpty ? null : webConfigNamespaces,
            rootDirectory: projectDir);
        return AspxSourceMappingService.FormatOutline(result);
    }
}

using System.Text;
using Microsoft.CodeAnalysis;
using RoslynMCP.Services;

namespace RoslynMCP.Tools.WebForms;

/// <summary>
/// Resolves symbols in ASPX files by parsing the WebForms markup tree and matching
/// the marked text to controls, properties, and events.
/// </summary>
internal class AspxGoToDefinition : IGoToDefinitionHandler
{
    public bool CanHandle(string filePath) => AspxSourceMappingService.IsAspxFile(filePath);

    public async Task<string> ResolveAsync(
        string systemPath, string markupSnippet, int contextLines, CancellationToken cancellationToken)
    {
        if (!MarkupString.TryParse(markupSnippet, out var markup, out string? parseError))
            return $"Error: Invalid markup snippet. {parseError}";

        if (!File.Exists(systemPath))
            return $"Error: File {systemPath} does not exist.";

        string? projectPath = await NonCSharpProjectFinder.FindProjectAsync(systemPath, cancellationToken);
        if (string.IsNullOrEmpty(projectPath))
            return "Error: Couldn't find a project containing this file.";

        var (workspace, project) = await WorkspaceService.GetOrOpenProjectAsync(
            projectPath, targetFilePath: systemPath, cancellationToken: cancellationToken);

        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation is null)
            return "Error: Unable to get compilation for the project.";

        string fileText = await File.ReadAllTextAsync(systemPath, cancellationToken);
        string? projectDir = Path.GetDirectoryName(projectPath);

        var webConfigNamespaces = projectDir is not null
            ? AspxSourceMappingService.LoadWebConfigNamespaces(projectDir)
            : default;

        var parseResult = AspxSourceMappingService.Parse(
            systemPath, fileText, compilation,
            namespaces: webConfigNamespaces.IsDefaultOrEmpty ? null : webConfigNamespaces,
            rootDirectory: projectDir);

        var symbol = AspxSourceMappingService.ResolveAspxSymbol(parseResult, fileText, markup!);
        if (symbol is null)
            return $"No symbol found for '{markup!.MarkedText}' in ASPX file.";

        return await GoToDefinitionTool.FormatDefinitionAsync(symbol, project, contextLines, cancellationToken);
    }
}

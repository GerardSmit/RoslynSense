using System.Text;
using Microsoft.CodeAnalysis;

namespace RoslynMCP.Services;

/// <summary>
/// Shared validation and resolution helpers for MCP tools.
/// Eliminates boilerplate validation that was duplicated across all 13 tools.
/// </summary>
internal static class ToolHelper
{
    /// <summary>
    /// Validates a file path, normalizes it, and resolves its containing project.
    /// Returns the workspace, project, and document for the file.
    /// </summary>
    public static async Task<ToolFileContext?> ResolveFileAsync(
        string? filePath,
        StringBuilder? errors,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            errors?.Append("Error: File path cannot be empty.");
            return null;
        }

        string systemPath = PathHelper.NormalizePath(filePath);
        if (!File.Exists(systemPath))
        {
            errors?.Append($"Error: File {systemPath} does not exist.");
            return null;
        }

        // When a .csproj is passed directly, use it as the project path
        // (many tool descriptions say "path to .csproj or any source file")
        string? projectPath;
        if (systemPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            projectPath = systemPath;
        }
        else
        {
            projectPath = await WorkspaceService.FindContainingProjectAsync(systemPath, cancellationToken);
        }

        if (string.IsNullOrEmpty(projectPath))
        {
            errors?.Append("Error: Couldn't find a project containing this file.");
            return null;
        }

        var (workspace, project) = await WorkspaceService.GetOrOpenProjectAsync(
            projectPath, targetFilePath: systemPath, cancellationToken: cancellationToken);

        var document = WorkspaceService.FindDocumentInProject(project, systemPath);

        return new ToolFileContext(systemPath, projectPath, workspace, project, document);
    }

    /// <summary>
    /// Validates file + markup snippet, resolves the symbol.
    /// Used by tools that need markup-based symbol resolution (GoToDefinition, FindUsages, etc.)
    /// </summary>
    public static async Task<ToolSymbolContext?> ResolveSymbolAsync(
        string? filePath,
        string? markupSnippet,
        StringBuilder errors,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(markupSnippet))
        {
            errors.Append("Error: markupSnippet cannot be empty.");
            return null;
        }

        if (!MarkupString.TryParse(markupSnippet, out var markup, out string? parseError))
        {
            errors.Append($"Error: Invalid markup snippet. {parseError}");
            return null;
        }

        var fileCtx = await ResolveFileAsync(filePath, errors, cancellationToken);
        if (fileCtx is null)
            return null;

        if (fileCtx.Document is null)
        {
            errors.Append("Error: File not found in project.");
            return null;
        }

        var resolution = await MarkupSymbolResolver.ResolveAsync(
            fileCtx.Document, fileCtx.Workspace, markup!, cancellationToken);

        return new ToolSymbolContext(fileCtx, markup!, resolution);
    }

    /// <summary>
    /// Formats a standard resolution error message.
    /// </summary>
    public static string FormatResolutionError(MarkupResolutionResult resolution)
    {
        return resolution.Kind switch
        {
            MarkupResolutionResult.ResultKind.NoSymbol =>
                $"No symbol found at markup target. {resolution.Message}",
            MarkupResolutionResult.ResultKind.Ambiguous =>
                $"Ambiguous markup match. {resolution.Message}",
            MarkupResolutionResult.ResultKind.NoMatch =>
                $"Snippet not found in file. {resolution.Message}",
            _ => $"Error: {resolution.Message}",
        };
    }
}

/// <summary>
/// Result of resolving a file path to its project context.
/// </summary>
internal sealed record ToolFileContext(
    string SystemPath,
    string ProjectPath,
    Workspace Workspace,
    Project Project,
    Document? Document)
{
    public string? ProjectDir => Path.GetDirectoryName(ProjectPath);
}

/// <summary>
/// Result of resolving a markup snippet to a symbol.
/// </summary>
internal sealed record ToolSymbolContext(
    ToolFileContext File,
    MarkupString Markup,
    MarkupResolutionResult Resolution)
{
    public bool IsResolved => Resolution.Kind == MarkupResolutionResult.ResultKind.Resolved;
    public ISymbol? Symbol => Resolution.Symbol;
    public Document Document => File.Document!;
    public Project Project => File.Project;
    public Workspace Workspace => File.Workspace;
    public string SystemPath => File.SystemPath;
    public string? ProjectDir => File.ProjectDir;
}

using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynMCP.Services.ProjectModel;

namespace RoslynMCP.Tools;

/// <summary>
/// Structural edits to the solution, so the AI stops shelling out to <c>dotnet</c> for them.
/// A CLI call from a chat mutates the project files behind the daemon's loaded workspace, which
/// then answers from a stale compilation until something else forces a reload; these run inside
/// the daemon and invalidate what they changed.
/// </summary>
[McpServerToolType]
public static class ProjectMutationTool
{
    [McpServerTool, Description(
        "Add a project-to-project reference. Refuses a reference that would create a cycle, and " +
        "reloads the workspace so later analysis sees the new dependency.")]
    public static async Task<string> AddProjectReference(
        [Description("Project that will depend on the other (path to its .csproj).")]
        string projectPath,
        [Description("Project being referenced (path to its .csproj).")]
        string referencedProjectPath,
        CancellationToken cancellationToken = default)
    {
        var result = await ProjectMutationService.AddProjectReferenceAsync(
            projectPath, referencedProjectPath, cancellationToken);
        return result.Ok ? result.Message : $"Error: {result.Message}";
    }

    [McpServerTool, Description("Remove a project-to-project reference.")]
    public static async Task<string> RemoveProjectReference(
        [Description("Project the reference is declared in.")] string projectPath,
        [Description("Project being referenced.")] string referencedProjectPath,
        CancellationToken cancellationToken = default)
    {
        var result = await ProjectMutationService.RemoveProjectReferenceAsync(
            projectPath, referencedProjectPath, cancellationToken);
        return result.Ok ? result.Message : $"Error: {result.Message}";
    }

    [McpServerTool, Description(
        "Create a new source file in a project, with its namespace inferred from the folder it " +
        "lands in. Adds a Compile item when the project does not glob its sources.")]
    public static async Task<string> AddFile(
        [Description("Project the file belongs to.")] string projectPath,
        [Description("Path relative to the project directory, e.g. 'Orders/OrderTotal.cs'.")]
        string relativePath,
        [Description("What to scaffold: class (default), interface, record, enum, or empty.")]
        string kind = "class",
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<ProjectMutationService.FileKind>(kind, ignoreCase: true, out var fileKind))
            return $"Error: unknown kind '{kind}'. Use class, interface, record, enum, or empty.";

        var result = await ProjectMutationService.AddFileAsync(
            projectPath, relativePath, fileKind, cancellationToken);
        return result.Ok ? result.Message : $"Error: {result.Message}";
    }

    [McpServerTool, Description(
        "Delete a source file and remove its project item. Deleting the file alone leaves a " +
        "dangling Compile item in projects that list their sources explicitly.")]
    public static async Task<string> DeleteFile(
        [Description("Full path of the file to delete.")] string filePath,
        CancellationToken cancellationToken = default)
    {
        var result = await ProjectMutationService.DeleteFileAsync(filePath, cancellationToken);
        return result.Ok ? result.Message : $"Error: {result.Message}";
    }

    [McpServerTool, Description(
        "Create a project from a dotnet template (classlib, console, xunit, webapi, ...) and add " +
        "it to the open solution.")]
    public static async Task<string> CreateProject(
        [Description("Template short name, e.g. 'classlib', 'xunit', 'webapi'.")] string template,
        [Description("Project name; also the folder created for it.")] string name,
        [Description("Directory to create the project folder in.")] string directory,
        [Description("Target framework, e.g. 'net10.0'. Omit for the template's default.")]
        string? targetFramework = null,
        [Description("Add the project to the open solution (default: true).")]
        bool addToSolution = true,
        CancellationToken cancellationToken = default)
    {
        var result = await ProjectMutationService.CreateProjectAsync(
            template, name, directory, targetFramework, addToSolution, cancellationToken);
        return result.Ok ? result.Message : $"Error: {result.Message}";
    }

    [McpServerTool, Description("Add an existing project to the open solution.")]
    public static async Task<string> AddProjectToSolution(
        [Description("Path to the project file.")] string projectPath,
        [Description("Solution to add it to. Omit for the open one.")] string? solutionPath = null,
        CancellationToken cancellationToken = default)
    {
        var result = await ProjectMutationService.AddProjectToSolutionAsync(
            projectPath, solutionPath, cancellationToken);
        return result.Ok ? result.Message : $"Error: {result.Message}";
    }
}

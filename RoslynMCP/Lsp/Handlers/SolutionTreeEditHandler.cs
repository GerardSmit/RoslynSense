using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Services.ProjectModel;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>
/// Edits made from the Solution Explorer: new file or folder, delete, rename, and drag-and-drop.
/// </summary>
/// <remarks>
/// These run in the daemon rather than in the extension so the workspace learns about the change
/// as it happens, and so a rename gets the same namespace and type fixups
/// (<see cref="FileOperationsHandler"/>) the editor's own rename does.
/// </remarks>
internal static class SolutionTreeEditHandler
{
    public static async Task<SolutionTreeEditResult> EditAsync(
        SolutionTreeEditParams p, CancellationToken ct)
    {
        try
        {
            return p.Action switch
            {
                "addFile" => await AddFileAsync(p, ct),
                "addFolder" => AddFolder(p),
                "delete" => await DeleteAsync(p, ct),
                "rename" => await RenameAsync(p, ct),
                "move" => await MoveAsync(p, ct),
                _ => new SolutionTreeEditResult(false, $"Unknown action '{p.Action}'."),
            };
        }
        catch (Exception ex)
        {
            return new SolutionTreeEditResult(false, ex.Message);
        }
    }

    private static async Task<SolutionTreeEditResult> AddFileAsync(
        SolutionTreeEditParams p, CancellationToken ct)
    {
        if (p.Name is not { Length: > 0 } name)
            return new SolutionTreeEditResult(false, "A file name is required.");

        string? directory = DirectoryOf(p.TargetUri);
        string? project = p.ProjectPath ?? (directory is null ? null : FindProject(directory));
        if (project is null || directory is null)
            return new SolutionTreeEditResult(false, "Could not tell which project the file belongs to.");

        var kind = Enum.TryParse<ProjectMutationService.FileKind>(p.Kind, ignoreCase: true, out var parsed)
            ? parsed
            : ProjectMutationService.FileKind.Class;

        string relative = Path.GetRelativePath(Path.GetDirectoryName(project)!, Path.Combine(directory, name));
        var result = await ProjectMutationService.AddFileAsync(project, relative, kind, ct);

        string created = Path.Combine(directory, Path.HasExtension(name) ? name : name + ".cs");
        return new SolutionTreeEditResult(
            result.Ok, result.Message, result.Ok ? LspConverters.PathToUri(created) : null);
    }

    private static SolutionTreeEditResult AddFolder(SolutionTreeEditParams p)
    {
        if (p.Name is not { Length: > 0 } name)
            return new SolutionTreeEditResult(false, "A folder name is required.");
        if (DirectoryOf(p.TargetUri) is not { } parent)
            return new SolutionTreeEditResult(false, "Could not tell where to create the folder.");

        string full = Path.Combine(parent, name);
        if (Directory.Exists(full))
            return new SolutionTreeEditResult(false, $"{name} already exists.");

        Directory.CreateDirectory(full);

        // No workspace invalidation: an empty directory changes no compilation. It becomes real
        // to the project the moment a file lands in it, which does invalidate.
        return new SolutionTreeEditResult(true, $"Created {name}.", LspConverters.PathToUri(full));
    }

    private static async Task<SolutionTreeEditResult> DeleteAsync(
        SolutionTreeEditParams p, CancellationToken ct)
    {
        if (LspConverters.UriToPath(p.TargetUri ?? "") is not { } path)
            return new SolutionTreeEditResult(false, "Nothing to delete.");

        if (Directory.Exists(path))
        {
            var files = Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories).ToList();
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (Exception ex)
            {
                return new SolutionTreeEditResult(false, $"Could not delete the folder: {ex.Message}");
            }

            if (files.Count > 0 && FindProject(path) is { } owner)
                ProjectEvaluationService.Evict(owner);
            await WorkspaceService.EvictAllAsync(ct);

            return new SolutionTreeEditResult(true, $"Deleted {Path.GetFileName(path)}.");
        }

        var result = await ProjectMutationService.DeleteFileAsync(path, ct);
        return new SolutionTreeEditResult(result.Ok, result.Message);
    }

    private static async Task<SolutionTreeEditResult> RenameAsync(
        SolutionTreeEditParams p, CancellationToken ct)
    {
        if (LspConverters.UriToPath(p.TargetUri ?? "") is not { } path)
            return new SolutionTreeEditResult(false, "Nothing to rename.");
        if (p.Name is not { Length: > 0 } name)
            return new SolutionTreeEditResult(false, "A new name is required.");

        string destination = Path.Combine(Path.GetDirectoryName(path)!, name);
        return await MoveOrRenameAsync(path, destination, ct);
    }

    private static async Task<SolutionTreeEditResult> MoveAsync(
        SolutionTreeEditParams p, CancellationToken ct)
    {
        if (LspConverters.UriToPath(p.TargetUri ?? "") is not { } source)
            return new SolutionTreeEditResult(false, "Nothing to move.");
        if (DirectoryOf(p.DestinationUri) is not { } directory)
            return new SolutionTreeEditResult(false, "Could not tell where to move it.");

        return await MoveOrRenameAsync(source, Path.Combine(directory, Path.GetFileName(source)), ct);
    }

    /// <summary>
    /// Moves a file or directory and lets the rename fixups follow it.
    /// </summary>
    /// <remarks>
    /// Renaming and moving are the same operation to the file system, and to the namespace
    /// correspondence they both break; only the resulting path differs.
    /// </remarks>
    private static async Task<SolutionTreeEditResult> MoveOrRenameAsync(
        string source, string destination, CancellationToken ct)
    {
        if (Path.GetFullPath(source).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            return new SolutionTreeEditResult(true, "Nothing changed.");
        if (File.Exists(destination) || Directory.Exists(destination))
            return new SolutionTreeEditResult(false, $"{Path.GetFileName(destination)} already exists.");

        bool isDirectory = Directory.Exists(source);
        if (!isDirectory && !File.Exists(source))
            return new SolutionTreeEditResult(false, $"{Path.GetFileName(source)} no longer exists.");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (isDirectory)
                Directory.Move(source, destination);
            else
                File.Move(source, destination);
        }
        catch (Exception ex)
        {
            return new SolutionTreeEditResult(false, $"Could not move it: {ex.Message}");
        }

        string? project = FindProject(destination);
        if (project is not null)
        {
            RewriteItem(project, source, destination);
            ProjectEvaluationService.Evict(project);
        }
        await WorkspaceService.EvictAllAsync(ct);

        // The type and namespace fixups are the LSP's own, so a tree rename and an editor rename
        // leave the code in the same state.
        var edit = isDirectory
            ? null
            : await FileOperationsHandler.WillRenameAsync(
                new RenameFilesParams([
                    new FileRename(LspConverters.PathToUri(source), LspConverters.PathToUri(destination)),
                ]), ct);

        string message = $"Renamed to {Path.GetFileName(destination)}";
        int touched = edit?.Changes?.Count ?? 0;
        if (touched > 0)
            message += $"; updated {touched} file{(touched == 1 ? "" : "s")}";

        return new SolutionTreeEditResult(
            true, message + ".", LspConverters.PathToUri(destination), edit);
    }

    /// <summary>Points an explicit project item at the file's new path.</summary>
    private static void RewriteItem(string projectPath, string oldPath, string newPath)
    {
        try
        {
            string text = File.ReadAllText(projectPath);
            string directory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
            string oldInclude = Path.GetRelativePath(directory, oldPath);
            string newInclude = Path.GetRelativePath(directory, newPath);

            if (!text.Contains(oldInclude, StringComparison.OrdinalIgnoreCase))
                return;

            File.WriteAllText(projectPath, text.Replace(oldInclude, newInclude, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            ServiceLog.Warn(
                $"Could not update the item path in '{Path.GetFileName(projectPath)}': {ex.Message}",
                key: $"item-rewrite:{projectPath}");
        }
    }

    private static string? DirectoryOf(string? uri)
    {
        if (uri is null || LspConverters.UriToPath(uri) is not { } path)
            return null;

        return Directory.Exists(path) ? path : Path.GetDirectoryName(path);
    }

    private static string? FindProject(string path)
    {
        var directory = new DirectoryInfo(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!);
        while (directory is not null)
        {
            string? project = Directory
                .EnumerateFiles(directory.FullName, "*.*proj", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
            if (project is not null)
                return project;
            directory = directory.Parent;
        }
        return null;
    }
}

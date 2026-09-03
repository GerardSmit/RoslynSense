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
                "copy" => await CopyAsync(p, ct),
                "addSolutionFolder" => AddSolutionFolder(p),
                "renameSolutionFolder" => await RenameSolutionFolderAsync(p, ct),
                "removeSolutionFolder" => await RemoveSolutionFolderAsync(p, ct),
                "moveProject" => await MoveProjectAsync(p, ct),
                "addSolutionItem" => await SolutionItemAsync(p, attach: true, ct),
                "removeSolutionItem" => await SolutionItemAsync(p, attach: false, ct),
                "addProjectReference" => await AddProjectReferenceAsync(p, ct),
                "removeProjectReference" => await RemoveProjectReferenceAsync(p, ct),
                "addProject" => await AddProjectAsync(p, ct),
                "addExistingProject" => await AddExistingProjectAsync(p, ct),
                "removeProject" => await RemoveProjectAsync(p, ct),
                "includeExistingFile" => await IncludeExistingFileAsync(p, ct),
                "excludeFile" => await ExcludeFileAsync(p, ct),
                "addAssemblyReference" => await AddAssemblyReferenceAsync(p, ct),
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
    /// References one project from another, from the Dependencies node.
    /// </summary>
    private static async Task<SolutionTreeEditResult> AddProjectReferenceAsync(
        SolutionTreeEditParams p, CancellationToken ct)
    {
        if (p.ProjectPath is not { Length: > 0 } project)
            return new SolutionTreeEditResult(false, "Could not tell which project to add to.");
        if (p.DestinationUri is null || LspConverters.UriToPath(p.DestinationUri) is not { } referenced)
            return new SolutionTreeEditResult(false, "Could not tell which project to reference.");

        var result = await ProjectMutationService.AddProjectReferenceAsync(project, referenced, ct);
        if (result.Ok)
            ProjectEvaluationService.Evict(project);

        return new SolutionTreeEditResult(result.Ok, result.Message);
    }

    /// <summary>
    /// Adds a .NET Framework assembly reference from the Dependencies node.
    /// </summary>
    private static async Task<SolutionTreeEditResult> AddAssemblyReferenceAsync(
        SolutionTreeEditParams p, CancellationToken ct)
    {
        if (p.ProjectPath is not { Length: > 0 } project)
            return new SolutionTreeEditResult(false, "Could not tell which project to add to.");
        if (p.Name is not { Length: > 0 } assembly)
            return new SolutionTreeEditResult(false, "An assembly name is required.");

        var result = await ProjectMutationService.AddAssemblyReferenceAsync(project, assembly, ct);
        if (result.Ok)
            ProjectEvaluationService.Evict(project);

        return new SolutionTreeEditResult(result.Ok, result.Message);
    }

    /// <summary>
    /// Creates a project from a <c>dotnet new</c> template and puts it in the solution — inside
    /// a solution folder when that is where the command was invoked.
    /// </summary>
    /// <remarks>
    /// <c>dotnet sln add</c> can place a project in a solution folder, but only by a path it
    /// derives itself, so nesting is written here instead: the same two mechanisms the folder
    /// itself uses, a <c>NestedProjects</c> entry for <c>.sln</c> and containment for
    /// <c>.slnx</c>.
    /// </remarks>
    private static async Task<SolutionTreeEditResult> AddProjectAsync(
        SolutionTreeEditParams p, CancellationToken ct)
    {
        if (p.Name is not { Length: > 0 } name)
            return new SolutionTreeEditResult(false, "A project name is required.");
        if (p.Kind is not { Length: > 0 } template)
            return new SolutionTreeEditResult(false, "A template is required.");
        if (ResolveSolution(p.TargetUri) is not { } solutionPath)
            return new SolutionTreeEditResult(false, "Could not tell which solution to add to.");

        // CreateProjectAsync appends the name itself, so this is the directory to create it in
        // rather than the project's own.
        string solutionDirectory = Path.GetDirectoryName(solutionPath)!;

        // addToSolution: false because its own path picks the most recently loaded solution,
        // which is not necessarily the one whose tree this came from.
        var result = await ProjectMutationService.CreateProjectAsync(
            template, name, solutionDirectory, p.TargetFramework, addToSolution: false, ct);
        if (!result.Ok)
            return new SolutionTreeEditResult(false, result.Message);

        string projectDirectory = Path.Combine(solutionDirectory, name);
        string? created = Directory.Exists(projectDirectory)
            ? Directory.EnumerateFiles(projectDirectory, "*.*proj", SearchOption.TopDirectoryOnly)
                .FirstOrDefault()
            : null;

        if (created is null)
            return new SolutionTreeEditResult(false, $"{result.Message} No project file was produced.");

        var added = await ProjectMutationService.AddProjectToSolutionAsync(created, solutionPath, ct);
        if (!added.Ok)
            return new SolutionTreeEditResult(false, $"{result.Message} {added.Message}");

        // p.ProjectPath carries the solution folder to nest under, when there is one.
        if (p.ProjectPath is { Length: > 0 } folderId)
        {
            try
            {
                SolutionFileWriter.MoveProject(solutionPath, created, folderId);
            }
            catch (Exception ex)
            {
                return new SolutionTreeEditResult(
                    true, $"{result.Message} It could not be moved into the folder: {ex.Message}");
            }
        }

        await WorkspaceService.EvictAllAsync(ct);

        return new SolutionTreeEditResult(
            true, $"{result.Message} {added.Message}", LspConverters.PathToUri(created));
    }

    /// <summary>
    /// Adds a project that already exists on disk to the solution, optionally inside a folder.
    /// </summary>
    private static async Task<SolutionTreeEditResult> AddExistingProjectAsync(
        SolutionTreeEditParams p, CancellationToken ct)
    {
        if (p.DestinationUri is null || LspConverters.UriToPath(p.DestinationUri) is not { } project)
            return new SolutionTreeEditResult(false, "Could not tell which project to add.");
        if (!File.Exists(project))
            return new SolutionTreeEditResult(false, $"{Path.GetFileName(project)} does not exist.");
        if (ResolveSolution(p.TargetUri) is not { } solutionPath)
            return new SolutionTreeEditResult(false, "Could not tell which solution to add to.");

        var added = await ProjectMutationService.AddProjectToSolutionAsync(project, solutionPath, ct);
        if (!added.Ok)
            return new SolutionTreeEditResult(false, added.Message);

        if (p.ProjectPath is { Length: > 0 } folderId)
        {
            try
            {
                SolutionFileWriter.MoveProject(solutionPath, project, folderId);
            }
            catch (Exception ex)
            {
                return new SolutionTreeEditResult(
                    true, $"{added.Message} It could not be moved into the folder: {ex.Message}");
            }
        }

        await WorkspaceService.EvictAllAsync(ct);
        return new SolutionTreeEditResult(true, added.Message, LspConverters.PathToUri(project));
    }

    /// <summary>
    /// Takes a project out of the solution. The project and its files stay on disk — this is the
    /// solution's list of members, not the files themselves.
    /// </summary>
    private static async Task<SolutionTreeEditResult> RemoveProjectAsync(
        SolutionTreeEditParams p, CancellationToken ct)
    {
        if (LspConverters.UriToPath(p.TargetUri ?? "") is not { } project)
            return new SolutionTreeEditResult(false, "Could not tell which project to remove.");
        if (ResolveSolution(p.DestinationUri) is not { } solutionPath)
            return new SolutionTreeEditResult(false, "Could not tell which solution to remove it from.");

        var result = await ProjectMutationService.RemoveProjectFromSolutionAsync(
            project, solutionPath, ct);
        if (result.Ok)
            await WorkspaceService.EvictAllAsync(ct);

        return new SolutionTreeEditResult(result.Ok, result.Message);
    }

    /// <summary>
    /// Moves a project between solution folders, or out to the solution root.
    /// </summary>
    /// <remarks>
    /// Nothing on disk moves. A solution folder is a grouping written into the solution file, so
    /// the project file stays where it is and only its parent link changes — which is also why
    /// this cannot go through the drag-and-drop <c>move</c> action, whose whole job is to relocate
    /// files.
    /// </remarks>
    private static async Task<SolutionTreeEditResult> MoveProjectAsync(
        SolutionTreeEditParams p, CancellationToken ct)
    {
        if (LspConverters.UriToPath(p.TargetUri ?? "") is not { } project)
            return new SolutionTreeEditResult(false, "Could not tell which project to move.");
        if (ResolveSolution(p.DestinationUri) is not { } solutionPath)
            return new SolutionTreeEditResult(false, "Could not tell which solution to edit.");

        try
        {
            SolutionFileWriter.MoveProject(solutionPath, project, Folder(p.ProjectPath));
        }
        catch (Exception ex)
        {
            return new SolutionTreeEditResult(false, $"Could not move the project: {ex.Message}");
        }

        // Deliberately nothing to invalidate: a solution folder is a grouping written into
        // the .sln, so no project file moves, the project set is identical, and every
        // compilation stays valid. This used to evict every loaded workspace in the process.

        return new SolutionTreeEditResult(
            true,
            p.ProjectPath is { Length: > 0 }
                ? $"Moved {Path.GetFileNameWithoutExtension(project)} into {p.Name ?? "the folder"}."
                : $"Moved {Path.GetFileNameWithoutExtension(project)} to the solution root.");
    }

    /// <summary>Attaches or detaches a solution item — a file a solution folder carries.</summary>
    private static async Task<SolutionTreeEditResult> SolutionItemAsync(
        SolutionTreeEditParams p, bool attach, CancellationToken ct)
    {
        if (LspConverters.UriToPath(p.TargetUri ?? "") is not { } file)
            return new SolutionTreeEditResult(false, "Could not tell which file.");
        if (p.ProjectPath is not { Length: > 0 } folderId)
            return new SolutionTreeEditResult(false, "Could not tell which solution folder.");
        if (ResolveSolution(p.DestinationUri) is not { } solutionPath)
            return new SolutionTreeEditResult(false, "Could not tell which solution to edit.");
        if (attach && Directory.Exists(file))
            return new SolutionTreeEditResult(
                false, "A solution folder can hold files, but not folders.");
        if (attach && !File.Exists(file))
            return new SolutionTreeEditResult(false, $"{Path.GetFileName(file)} does not exist.");

        try
        {
            if (attach)
                SolutionFileWriter.AddSolutionItem(solutionPath, folderId, file);
            else
                SolutionFileWriter.RemoveSolutionItem(solutionPath, folderId, file);
        }
        catch (Exception ex)
        {
            return new SolutionTreeEditResult(false, $"Could not edit the solution: {ex.Message}");
        }

        // Deliberately nothing to invalidate: a solution folder is a grouping written into
        // the .sln, so no project file moves, the project set is identical, and every
        // compilation stays valid. This used to evict every loaded workspace in the process.

        return new SolutionTreeEditResult(
            true,
            attach
                ? $"Added {Path.GetFileName(file)} to the solution folder."
                : $"Removed {Path.GetFileName(file)} from the solution folder.",
            LspConverters.PathToUri(file));
    }

    private static async Task<SolutionTreeEditResult> RenameSolutionFolderAsync(
        SolutionTreeEditParams p, CancellationToken ct)
    {
        if (p.ProjectPath is not { Length: > 0 } folderId)
            return new SolutionTreeEditResult(false, "Could not tell which solution folder.");
        if (p.Name is not { Length: > 0 } name)
            return new SolutionTreeEditResult(false, "A new name is required.");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return new SolutionTreeEditResult(false, $"'{name}' is not a valid folder name.");
        if (ResolveSolution(p.TargetUri) is not { } solutionPath)
            return new SolutionTreeEditResult(false, "Could not tell which solution to edit.");

        try
        {
            SolutionFileWriter.RenameFolder(solutionPath, folderId, name);
        }
        catch (Exception ex)
        {
            return new SolutionTreeEditResult(false, $"Could not rename the folder: {ex.Message}");
        }

        // Deliberately nothing to invalidate: a solution folder is a grouping written into
        // the .sln, so no project file moves, the project set is identical, and every
        // compilation stays valid. This used to evict every loaded workspace in the process.
        return new SolutionTreeEditResult(true, $"Renamed the folder to {name}.");
    }

    private static async Task<SolutionTreeEditResult> RemoveSolutionFolderAsync(
        SolutionTreeEditParams p, CancellationToken ct)
    {
        if (p.ProjectPath is not { Length: > 0 } folderId)
            return new SolutionTreeEditResult(false, "Could not tell which solution folder.");
        if (ResolveSolution(p.TargetUri) is not { } solutionPath)
            return new SolutionTreeEditResult(false, "Could not tell which solution to edit.");

        int detached;
        try
        {
            detached = SolutionFileWriter.RemoveFolder(solutionPath, folderId);
        }
        catch (Exception ex)
        {
            return new SolutionTreeEditResult(false, $"Could not remove the folder: {ex.Message}");
        }

        // Deliberately nothing to invalidate: a solution folder is a grouping written into
        // the .sln, so no project file moves, the project set is identical, and every
        // compilation stays valid. This used to evict every loaded workspace in the process.
        return new SolutionTreeEditResult(
            true,
            detached == 0
                ? "Removed the solution folder; what was inside it moved up a level."
                : "Removed the solution folder; projects moved up a level and " +
                  $"{(detached == 1 ? "1 solution item" : $"{detached} solution items")} " +
                  "stopped being listed. The files are still on disk.");
    }

    /// <summary>Adds a file that is already on disk to the project that owns its directory.</summary>
    private static async Task<SolutionTreeEditResult> IncludeExistingFileAsync(
        SolutionTreeEditParams p, CancellationToken ct)
    {
        if (LspConverters.UriToPath(p.TargetUri ?? "") is not { } file)
            return new SolutionTreeEditResult(false, "Could not tell which file to add.");
        if (!File.Exists(file))
            return new SolutionTreeEditResult(false, $"{Path.GetFileName(file)} does not exist.");

        string? project = p.ProjectPath ?? FindProject(file);
        if (project is null)
            return new SolutionTreeEditResult(false, "Could not tell which project the file belongs to.");

        await ProjectMutationService.IncludeExistingFileAsync(project, file, ct);
        ProjectEvaluationService.Evict(project);

        // One document joined the project; nothing else in the solution changed.
        if (await WorkspaceService.TryApplyFileChangeAsync(
                project, file, FileChange.Created, ct, authoritative: true) == FileSyncResult.CannotApply)
        {
            await WorkspaceService.EvictProjectAsync(project, ct);
        }

        return new SolutionTreeEditResult(
            true, $"Added {Path.GetFileName(file)}.", LspConverters.PathToUri(file));
    }

    /// <summary>
    /// Drops a file from its project without deleting it — Visual Studio's "Exclude From Project".
    /// </summary>
    private static async Task<SolutionTreeEditResult> ExcludeFileAsync(
        SolutionTreeEditParams p, CancellationToken ct)
    {
        if (LspConverters.UriToPath(p.TargetUri ?? "") is not { } file)
            return new SolutionTreeEditResult(false, "Could not tell which file to exclude.");

        string? project = p.ProjectPath ?? FindProject(file);
        if (project is null)
            return new SolutionTreeEditResult(false, "Could not tell which project the file belongs to.");

        var result = await ProjectMutationService.ExcludeFileAsync(project, file, ct);
        if (result.Ok)
        {
            ProjectEvaluationService.Evict(project);

            // Excluding writes a Compile Remove, so the file is on disk but out of the project.
            // Dropping the document says exactly that, and leaves the rest of the solution alone.
            if (await WorkspaceService.TryApplyFileChangeAsync(
                    project, file, FileChange.Deleted, ct, authoritative: true) == FileSyncResult.CannotApply)
            {
                await WorkspaceService.EvictProjectAsync(project, ct);
            }
        }

        return new SolutionTreeEditResult(result.Ok, result.Message);
    }

    /// <summary>Removes a project-to-project reference from the Dependencies node.</summary>
    private static async Task<SolutionTreeEditResult> RemoveProjectReferenceAsync(
        SolutionTreeEditParams p, CancellationToken ct)
    {
        if (p.ProjectPath is not { Length: > 0 } project)
            return new SolutionTreeEditResult(false, "Could not tell which project to edit.");
        if (p.DestinationUri is null || LspConverters.UriToPath(p.DestinationUri) is not { } referenced)
            return new SolutionTreeEditResult(false, "Could not tell which reference to remove.");

        var result = await ProjectMutationService.RemoveProjectReferenceAsync(project, referenced, ct);
        if (result.Ok)
        {
            ProjectEvaluationService.Evict(project);

            // References come from MSBuild evaluation, so this genuinely needs a reload — but of
            // the workspace serving this project, not of every solution the process has open.
            await WorkspaceService.EvictProjectAsync(project, ct);
        }

        return new SolutionTreeEditResult(result.Ok, result.Message);
    }

    /// <summary>An empty folder id means the solution root, which the writer spells as null.</summary>
    private static string? Folder(string? folderId) =>
        folderId is { Length: > 0 } ? folderId : null;

    /// <summary>
    /// The solution to edit: the one the caller named, or the one that is open.
    /// </summary>
    /// <remarks>
    /// The client should not have to know this at all — the server is what bound the solution in
    /// the first place, and a tree node's id is only ever an echo of it. Trusting that echo made
    /// "add solution folder" fail with "the solution file no longer exists" whenever the id and
    /// the bound path disagreed, which the user cannot act on and cannot even see.
    /// </remarks>
    private static string? ResolveSolution(string? targetUri)
    {
        if (targetUri is { Length: > 0 } &&
            LspConverters.UriToPath(targetUri) is { Length: > 0 } named &&
            File.Exists(named))
        {
            return named;
        }

        return WorkspaceService.BoundSolutionPath is { Length: > 0 } bound && File.Exists(bound)
            ? bound
            : null;
    }

    /// <summary>
    /// Adds a solution folder — a grouping that exists in the solution file and not on disk.
    /// </summary>
    /// <remarks>
    /// The two formats express it differently enough to be worth writing out: <c>.sln</c> needs
    /// a <c>Project(...)</c> block under the solution-folder type GUID, and a nested one also
    /// needs an entry in <c>NestedProjects</c>; <c>.slnx</c> needs one element. Neither is a
    /// directory, which is why this is a separate action from "new folder" rather than the same
    /// one pointed at the solution.
    /// </remarks>
    private static SolutionTreeEditResult AddSolutionFolder(SolutionTreeEditParams p)
    {
        if (p.Name is not { Length: > 0 } name)
            return new SolutionTreeEditResult(false, "A folder name is required.");
        if (ResolveSolution(p.TargetUri) is not { } solutionPath)
        {
            return new SolutionTreeEditResult(false,
                p.TargetUri is { Length: > 0 } asked
                    ? $"No solution file at '{LspConverters.UriToPath(asked)}', and none is open."
                    : "Could not tell which solution to add to.");
        }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return new SolutionTreeEditResult(false, $"'{name}' is not a valid folder name.");

        try
        {
            SolutionFileWriter.AddFolder(solutionPath, name, Folder(p.ProjectPath));
        }
        catch (Exception ex)
        {
            return new SolutionTreeEditResult(false, $"Could not add the folder: {ex.Message}");
        }

        return new SolutionTreeEditResult(true, $"Added solution folder {name}.");
    }

    /// <summary>
    /// Copies a file into a folder, giving it a free name when one is taken.
    /// </summary>
    /// <remarks>
    /// Paste and duplicate are the same operation — duplicate just pastes back into the folder
    /// the file came from, which is why both land here rather than each getting an action.
    /// The copy keeps the original's type name, so it does not compile until it is renamed;
    /// that is what Visual Studio does too, and renaming it afterwards runs the type fixups.
    /// </remarks>
    private static async Task<SolutionTreeEditResult> CopyAsync(
        SolutionTreeEditParams p, CancellationToken ct)
    {
        if (LspConverters.UriToPath(p.TargetUri ?? "") is not { } source)
            return new SolutionTreeEditResult(false, "Nothing to copy.");
        if (DirectoryOf(p.DestinationUri) is not { } directory)
            return new SolutionTreeEditResult(false, "Could not tell where to copy it.");
        if (!File.Exists(source))
            return new SolutionTreeEditResult(false, $"{Path.GetFileName(source)} no longer exists.");

        string destination = FreeName(directory, Path.GetFileName(source));

        try
        {
            Directory.CreateDirectory(directory);
            File.Copy(source, destination);
        }
        catch (Exception ex)
        {
            return new SolutionTreeEditResult(false, $"Could not copy it: {ex.Message}");
        }

        if (FindProject(destination) is { } project)
        {
            await ProjectMutationService.IncludeExistingFileAsync(project, destination, ct);
            ProjectEvaluationService.Evict(project);

            // One document appeared. Adding it to the live workspace keeps every compilation in
            // the solution, where evicting threw all of them away for a single new file.
            if (await WorkspaceService.TryApplyFileChangeAsync(
                    project, destination, FileChange.Created, ct, authoritative: true) == FileSyncResult.CannotApply)
            {
                await WorkspaceService.EvictProjectAsync(project, ct);
            }
        }

        return new SolutionTreeEditResult(
            true, $"Copied to {Path.GetFileName(destination)}.",
            LspConverters.PathToUri(destination));
    }

    /// <summary>Appends " copy", " copy 2", … until the name is free.</summary>
    private static string FreeName(string directory, string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);

        string candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
            return candidate;

        for (int i = 1; ; i++)
        {
            string suffix = i == 1 ? " copy" : $" copy {i}";
            candidate = Path.Combine(directory, stem + suffix + extension);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
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
            await ProjectMutationService.RenameFileItemAsync(source, destination, ct);
            ProjectEvaluationService.Evict(project);

            // A move is a document leaving one path and arriving at another, applied to the live
            // workspace so the rest of the solution stays compiled — but only when a document is
            // involved at all, and at either end. Renaming an .aspx or a .json changes no
            // compilation, so requiring the workspace to report work done would evict the whole
            // solution for a file it never had; and testing only the source would miss
            // Notes.txt → Notes.cs, leaving a brand-new C# file invisible with its .csproj write
            // suppressed as our own echo. Directories are handled by the branch below, which
            // cannot reason per document and so reloads.
            bool needsDocumentMove = !isDirectory
                && (WatchedFilesHandler.IsCompiledSource(source)
                    || WatchedFilesHandler.IsCompiledSource(destination));

            bool applied = needsDocumentMove
                && await WorkspaceService.TryApplyFileChangeAsync(
                    project, source, FileChange.Deleted, ct, authoritative: true) == FileSyncResult.Applied
                && await WorkspaceService.TryApplyFileChangeAsync(
                    project, destination, FileChange.Created, ct, authoritative: true) == FileSyncResult.Applied;

            if (needsDocumentMove && !applied)
                await WorkspaceService.EvictProjectAsync(project, ct);
            else if (isDirectory)
                await WorkspaceService.EvictProjectAsync(project, ct);
        }

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

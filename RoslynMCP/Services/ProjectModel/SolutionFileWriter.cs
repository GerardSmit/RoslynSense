using Microsoft.VisualStudio.SolutionPersistence.Model;

namespace RoslynMCP.Services.ProjectModel;

/// <summary>
/// Writes the solution file: its folders, what is nested inside what, and the loose files a
/// solution folder carries.
/// </summary>
/// <remarks>
/// Every operation is load, mutate, save through the same model
/// <see cref="SolutionFileService"/> reads, so <c>.sln</c> and <c>.slnx</c> need no separate
/// code paths — the serializer knows how each format spells the thing being asked for. That is
/// the whole reason not to edit these files as text: the two formats express nesting completely
/// differently, and both of them punish a near-miss with a solution that still parses but has
/// projects missing from it.
///
/// A folder is identified by its path (<c>/Outer/Inner/</c>), which is what the tree hands back
/// as a node id, and a project by its file on disk.
/// </remarks>
public static class SolutionFileWriter
{
    /// <summary>Adds a solution folder, optionally inside another one.</summary>
    public static void AddFolder(string solutionPath, string name, string? parentFolderId)
    {
        var model = SolutionFileService.Open(solutionPath);
        model.AddFolder($"{Container(parentFolderId)}{name}/");
        SolutionFileService.Save(solutionPath, model);
    }

    /// <summary>Renames a solution folder in place, keeping everything nested under it.</summary>
    public static void RenameFolder(string solutionPath, string folderId, string name)
    {
        var model = SolutionFileService.Open(solutionPath);
        var folder = FindFolder(model, folderId);

        // A folder's path is its identity, so a rename moves everything that named it as a
        // parent. The model does not rewrite descendants for us.
        string above = Parent(folder.Path);
        string destination = $"{above}{name}/";

        if (folder.Path == destination)
            return;

        if (Same(folder.Path, destination))
        {
            // Only the case differs. Paths are matched without regard to case, so a move straight
            // to the destination looks like a move to where the folder already is and does
            // nothing — while still reporting success. Going by way of a name nothing else holds
            // makes the change land, and the detour is written out before the second half: the
            // serializer keeps the element it already has for a name it considers unchanged, so
            // the old spelling survives unless the file stops mentioning it first.
            string staging = $"{above}{name}.{Guid.NewGuid():N}/";
            Reparent(model, folder, staging);
            SolutionFileService.Save(solutionPath, model);

            model = SolutionFileService.Open(solutionPath);
            folder = FindFolder(model, staging);
        }
        else if (model.SolutionFolders.Any(f => Same(f.Path, destination)))
        {
            // Reparenting onto an existing folder merges the two, which is a reasonable thing
            // for a move to do and never what a rename meant.
            throw new InvalidOperationException($"A folder named '{name}' is already here.");
        }

        Reparent(model, folder, destination);
        SolutionFileService.Save(solutionPath, model);
    }

    /// <summary>
    /// Removes a solution folder. Whatever was inside moves up to the folder's own parent rather
    /// than disappearing with it — a solution folder is a grouping, and removing a grouping is
    /// not a reason to lose projects.
    /// </summary>
    /// <returns>
    /// How many solution items were detached rather than moved up. Only a top-level folder can
    /// lose any: the solution has no place for a loose file outside a folder, so there is nowhere
    /// above for them to go. The caller says so rather than claiming nothing was lost.
    /// </returns>
    public static int RemoveFolder(string solutionPath, string folderId)
    {
        var model = SolutionFileService.Open(solutionPath);
        var folder = FindFolder(model, folderId);
        string above = Parent(folder.Path);

        // Children first, while the folder they name as a parent still exists.
        foreach (var child in ChildFoldersOf(model, folder))
            Reparent(model, child, $"{above}{child.ActualDisplayName}/");

        var destination = above == "/" ? null : model.FindFolder(above);
        foreach (var project in ProjectsIn(model, folder))
            project.MoveToFolder(destination);

        var files = folder.Files ?? [];
        if (destination is not null)
        {
            foreach (string file in files)
                destination.AddFile(file);
        }

        model.RemoveFolder(folder);
        SolutionFileService.Save(solutionPath, model);

        return destination is null ? files.Count : 0;
    }

    /// <summary>
    /// Moves a project into a solution folder, or out to the solution root when
    /// <paramref name="parentFolderId"/> is null. Nothing on disk moves — a solution folder is
    /// not a directory, and the project file stays exactly where it is.
    /// </summary>
    public static void MoveProject(string solutionPath, string projectPath, string? parentFolderId)
    {
        var model = SolutionFileService.Open(solutionPath);
        var project = FindProject(model, solutionPath, projectPath);

        project.MoveToFolder(parentFolderId is { Length: > 0 }
            ? FindFolder(model, parentFolderId)
            : null);

        SolutionFileService.Save(solutionPath, model);
    }

    /// <summary>Moves a solution folder into another one, or out to the root.</summary>
    public static void MoveFolder(string solutionPath, string folderId, string? parentFolderId)
    {
        var model = SolutionFileService.Open(solutionPath);
        var folder = FindFolder(model, folderId);

        string into = Container(parentFolderId);
        if (into.StartsWith(folder.Path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A folder cannot be moved into itself.");

        Reparent(model, folder, $"{into}{folder.ActualDisplayName}/");
        SolutionFileService.Save(solutionPath, model);
    }

    /// <summary>
    /// Attaches an existing file to a solution folder — what Visual Studio calls a solution item.
    /// The file is referenced where it lies; nothing is copied and nothing is compiled.
    /// </summary>
    public static void AddSolutionItem(string solutionPath, string folderId, string filePath)
    {
        var model = SolutionFileService.Open(solutionPath);
        var folder = FindFolder(model, folderId);
        string relative = Relative(solutionPath, filePath);

        if (!(folder.Files ?? []).Any(f => Same(f, relative)))
            folder.AddFile(relative);

        SolutionFileService.Save(solutionPath, model);
    }

    /// <summary>Detaches a file from a solution folder. The file itself is untouched.</summary>
    public static void RemoveSolutionItem(string solutionPath, string folderId, string filePath)
    {
        var model = SolutionFileService.Open(solutionPath);
        var folder = FindFolder(model, folderId);
        string relative = Relative(solutionPath, filePath);

        // Matched against what is actually written rather than against our own spelling of the
        // path: the file was added by whoever last edited the solution, and they may have used
        // the other slash.
        if ((folder.Files ?? []).FirstOrDefault(f => Same(f, relative)) is { } written)
            folder.RemoveFile(written);

        SolutionFileService.Save(solutionPath, model);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The folder something is being put *into*: the named one, or the solution root.</summary>
    private static string Container(string? folderId) =>
        folderId is { Length: > 0 } id ? Normalise(id) : "/";

    /// <summary>The folder one level *above* a path — the root, written "/", when there is none.</summary>
    private static string Parent(string folderPath) =>
        SolutionFileService.ParentPath(folderPath) ?? "/";

    private static SolutionFolderModel FindFolder(SolutionModel model, string folderId) =>
        model.SolutionFolders.FirstOrDefault(f => Same(f.Path, Normalise(folderId)))
        ?? throw new InvalidOperationException($"Could not find the folder '{folderId}'.");

    private static SolutionProjectModel FindProject(
        SolutionModel model, string solutionPath, string projectPath)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;

        return model.SolutionProjects.FirstOrDefault(p => SamePath(
                Path.Combine(directory, p.FilePath), projectPath))
            ?? throw new InvalidOperationException("That project is not in the solution.");
    }

    /// <summary>
    /// Moves a folder to a new path, taking its descendants with it.
    /// </summary>
    /// <remarks>
    /// The model has no rename: a folder's path <em>is</em> its identity. So the folder and every
    /// folder beneath it are recreated at the new path and their contents moved across, deepest
    /// last so a parent always exists before the thing that goes in it.
    /// </remarks>
    private static void Reparent(SolutionModel model, SolutionFolderModel folder, string destination)
    {
        if (Same(folder.Path, destination))
            return;

        string from = folder.Path;
        var moving = model.SolutionFolders
            .Where(f => Same(f.Path, from) || f.Path.StartsWith(from, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Path.Length)
            .ToList();

        foreach (var source in moving)
        {
            string target = destination + source.Path[from.Length..];
            var created = model.FindFolder(target) ?? model.AddFolder(target);

            foreach (var project in ProjectsIn(model, source))
                project.MoveToFolder(created);

            foreach (string file in source.Files ?? [])
                created.AddFile(file);
        }

        // Deepest first on the way out, so nothing is removed while it still has children.
        foreach (var source in Enumerable.Reverse(moving))
        {
            if (model.FindFolder(source.Path) is { } stale)
                model.RemoveFolder(stale);
        }
    }

    private static List<SolutionProjectModel> ProjectsIn(SolutionModel model, SolutionFolderModel folder) =>
        [.. model.SolutionProjects.Where(p => p.Parent is { } parent && Same(parent.Path, folder.Path))];

    private static List<SolutionFolderModel> ChildFoldersOf(SolutionModel model, SolutionFolderModel folder) =>
        [.. model.SolutionFolders.Where(f =>
            SolutionFileService.ParentPath(f.Path) is { } parent && Same(parent, folder.Path))];

    /// <summary>A folder path the way the format spells it: leading and trailing slash, both.</summary>
    private static string Normalise(string folderId)
    {
        string trimmed = folderId.Replace('\\', '/').Trim('/');
        return trimmed.Length == 0 ? "/" : $"/{trimmed}/";
    }

    private static string Relative(string solutionPath, string filePath) =>
        Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(solutionPath))!, filePath);

    private static bool Same(string a, string b) =>
        string.Equals(a.Replace('\\', '/'), b.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}

using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace RoslynMCP.Services.ProjectModel;

public sealed record MutationResult(bool Ok, string Message);

/// <summary>
/// Structural edits to the solution: project references, source files, and new projects.
/// </summary>
/// <remarks>
/// Every mutation ends by evicting the affected caches, because the alternative — the AI or the
/// tree shelling <c>dotnet add</c> directly — changes the project files behind the loaded
/// workspace's back and leaves every later answer stale until something else forces a reload.
///
/// References and project creation go through the CLI rather than XML edits: it is the only path
/// that understands solution folders, Central Package Management, and the template engine.
/// File adds are done here because there is no CLI for them.
/// </remarks>
public static class ProjectMutationService
{
    public static async Task<MutationResult> AddProjectReferenceAsync(
        string projectPath, string referencedProjectPath, CancellationToken ct = default)
    {
        if (!File.Exists(projectPath))
            return new MutationResult(false, $"Project not found: {projectPath}");
        if (!File.Exists(referencedProjectPath))
            return new MutationResult(false, $"Referenced project not found: {referencedProjectPath}");
        if (PathHelper.NormalizePath(projectPath).Equals(
                PathHelper.NormalizePath(referencedProjectPath), StringComparison.OrdinalIgnoreCase))
        {
            return new MutationResult(false, "A project cannot reference itself.");
        }

        // A reference cycle fails the build in a way that points at neither project, so it is
        // worth refusing here where the two are still named.
        if (await ReferencesTransitivelyAsync(referencedProjectPath, projectPath, ct))
        {
            return new MutationResult(false,
                $"{Path.GetFileNameWithoutExtension(referencedProjectPath)} already references " +
                $"{Path.GetFileNameWithoutExtension(projectPath)}; adding this would make a cycle.");
        }

        // `dotnet add reference` rejects a non-SDK project, so those are edited directly. The
        // legacy form also needs the referenced project's GUID and name, which MSBuild resolves
        // from the element rather than from the file — VS writes them, and tooling expects them.
        if (!IsSdkStyle(projectPath))
        {
            string? failure = AddLegacyProjectReference(projectPath, referencedProjectPath);
            if (failure is not null)
                return new MutationResult(false, failure);
        }
        else
        {
            var (exitCode, output) = await RunDotnetAsync(
                ["add", projectPath, "reference", referencedProjectPath], ct);

            if (exitCode != 0)
                return new MutationResult(false, FirstError(output));
        }

        await InvalidateAsync(ct, projectPath);
        return new MutationResult(true,
            $"{Path.GetFileNameWithoutExtension(projectPath)} now references " +
            $"{Path.GetFileNameWithoutExtension(referencedProjectPath)}.");
    }

    public static async Task<MutationResult> RemoveProjectReferenceAsync(
        string projectPath, string referencedProjectPath, CancellationToken ct = default)
    {
        if (!File.Exists(projectPath))
            return new MutationResult(false, $"Project not found: {projectPath}");

        if (!IsSdkStyle(projectPath))
        {
            RemoveLegacyProjectReference(projectPath, referencedProjectPath);
        }
        else
        {
            var (exitCode, output) = await RunDotnetAsync(
                ["remove", projectPath, "reference", referencedProjectPath], ct);

            if (exitCode != 0)
                return new MutationResult(false, FirstError(output));
        }

        await InvalidateAsync(ct, projectPath);
        return new MutationResult(true,
            $"{Path.GetFileNameWithoutExtension(projectPath)} no longer references " +
            $"{Path.GetFileNameWithoutExtension(referencedProjectPath)}.");
    }

    /// <summary>Whether the project uses the SDK format, which the dotnet CLI can edit.</summary>
    internal static bool IsSdkStyle(string projectPath)
    {
        try
        {
            var root = XDocument.Load(projectPath).Root;
            return root?.Attribute("Sdk") is not null ||
                   root?.Elements().Any(e => e.Name.LocalName == "Sdk") == true;
        }
        catch
        {
            return true; // assume modern; the CLI reports a better error than a guess would
        }
    }

    /// <returns><c>null</c> on success, or why it failed.</returns>
    private static string? AddLegacyProjectReference(string projectPath, string referencedProjectPath)
    {
        try
        {
            var document = XDocument.Load(projectPath);
            if (document.Root is null)
                return "The project file is empty.";

            var ns = document.Root.Name.Namespace;
            string projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
            string include = Path.GetRelativePath(projectDirectory, referencedProjectPath);

            bool alreadyThere = document.Root.Descendants(ns + "ProjectReference")
                .Any(r => string.Equals(r.Attribute("Include")?.Value, include, StringComparison.OrdinalIgnoreCase));
            if (alreadyThere)
                return null;

            var group = document.Root.Elements(ns + "ItemGroup")
                .FirstOrDefault(g => g.Elements(ns + "ProjectReference").Any());
            if (group is null)
            {
                group = new XElement(ns + "ItemGroup");
                document.Root.Add(group);
            }

            var reference = new XElement(ns + "ProjectReference", new XAttribute("Include", include));
            if (ReadProperty(referencedProjectPath, "ProjectGuid") is { Length: > 0 } guid)
            {
                reference.Add(
                    new XElement(ns + "Project", guid),
                    new XElement(ns + "Name", Path.GetFileNameWithoutExtension(referencedProjectPath)));
            }

            group.Add(reference);
            document.Save(projectPath);
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not edit '{Path.GetFileName(projectPath)}': {ex.Message}";
        }
    }

    private static void RemoveLegacyProjectReference(string projectPath, string referencedProjectPath)
    {
        try
        {
            var document = XDocument.Load(projectPath);
            if (document.Root is null)
                return;

            var ns = document.Root.Name.Namespace;
            string projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
            string include = Path.GetRelativePath(projectDirectory, referencedProjectPath);

            var stale = document.Root.Descendants(ns + "ProjectReference")
                .Where(r => string.Equals(
                    r.Attribute("Include")?.Value, include, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (stale.Count == 0)
                return;

            foreach (var reference in stale)
                reference.Remove();
            document.Save(projectPath);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn(
                $"Could not remove the project reference from '{Path.GetFileName(projectPath)}': {ex.Message}",
                key: $"project-reference:{projectPath}");
        }
    }

    /// <summary>What a new file should contain.</summary>
    public enum FileKind { Class, Interface, Record, Enum, Empty }

    public static async Task<MutationResult> AddFileAsync(
        string projectPath, string relativePath, FileKind kind = FileKind.Class,
        CancellationToken ct = default)
    {
        if (!File.Exists(projectPath))
            return new MutationResult(false, $"Project not found: {projectPath}");

        string projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        string fullPath = Path.GetFullPath(Path.Combine(projectDirectory, relativePath));

        // A path that climbs out of the project would be added to it but live somewhere else.
        if (!fullPath.StartsWith(projectDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return new MutationResult(false, "The file must be inside the project directory.");

        if (File.Exists(fullPath))
            return new MutationResult(false, $"{relativePath} already exists.");

        if (!Path.HasExtension(fullPath))
            fullPath += ".cs";

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(
            fullPath, Scaffold(projectPath, projectDirectory, fullPath, kind), ct);

        string? itemResult = EnsureCompileItem(projectPath, projectDirectory, fullPath);

        await InvalidateAsync(ct, projectPath);
        return new MutationResult(true,
            $"Created {Path.GetRelativePath(projectDirectory, fullPath)}" +
            (itemResult is null ? "." : $" and {itemResult}."));
    }

    public static async Task<MutationResult> DeleteFileAsync(
        string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            return new MutationResult(false, $"File not found: {filePath}");

        string full = Path.GetFullPath(filePath);
        string? owner = FindOwningProject(full);

        try
        {
            File.Delete(full);
        }
        catch (Exception ex)
        {
            return new MutationResult(false, $"Could not delete {Path.GetFileName(full)}: {ex.Message}");
        }

        if (owner is not null)
        {
            RemoveCompileItem(owner, full);
            await InvalidateAsync(ct, owner);
        }
        else
        {
            await InvalidateAsync(ct);
        }

        return new MutationResult(true, $"Deleted {Path.GetFileName(full)}.");
    }

    /// <summary>
    /// Fills in a <c>.cs</c> file that already exists but is empty, and makes sure the project
    /// compiles it. Returns the text that belongs in it, or null when there is nothing to do.
    /// </summary>
    /// <remarks>
    /// The editor's own explorer creates a bare, empty file — no namespace, no type — so a
    /// class made that way starts in the global namespace and every analyzer complains. This is
    /// the same scaffolding <see cref="AddFileAsync"/> writes, applied after the fact. The text
    /// is returned rather than written so the caller can put it in the editor's buffer, which is
    /// where the file is at that moment.
    /// </remarks>
    public static async Task<string?> ScaffoldNewFileAsync(
        string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath) || new FileInfo(filePath).Length > 0)
            return null;
        if (!filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return null;
        if (FindOwningProject(Path.GetFullPath(filePath)) is not { } project)
            return null;

        string full = Path.GetFullPath(filePath);
        string projectDirectory = Path.GetDirectoryName(Path.GetFullPath(project))!;

        // A generated or designer file gets its content from whatever generates it.
        string name = Path.GetFileName(full);
        if (name.Contains(".Designer.", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        EnsureCompileItem(project, projectDirectory, full);
        await InvalidateAsync(ct, project);

        return Scaffold(project, projectDirectory, full, FileKind.Class);
    }

    /// <summary>
    /// The framework assemblies a project could reference — <c>System.Xml</c>,
    /// <c>System.ServiceModel</c> and the rest of what Visual Studio's "Add Reference" lists
    /// under Assemblies.
    /// </summary>
    /// <remarks>
    /// Read from the reference-assembly directory for the project's own target framework rather
    /// than from a fixed list, so a project on net472 is not offered something that only exists
    /// on net48. Only .NET Framework targets have these: on modern .NET the framework arrives as
    /// a package reference and there is nothing to add.
    /// </remarks>
    public static IReadOnlyList<string> AvailableAssemblyReferences(string projectPath)
    {
        string? targetFramework = ReadProperty(projectPath, "TargetFrameworkVersion")
            ?? ReadProperty(projectPath, "TargetFramework");
        if (targetFramework is null || !targetFramework.Contains("4", StringComparison.Ordinal))
            return [];

        // "v4.8" in a legacy project, "net48" in an SDK-style one.
        string version = targetFramework.StartsWith('v')
            ? targetFramework
            : "v" + string.Join('.', targetFramework["net".Length..].ToCharArray());

        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Reference Assemblies", "Microsoft", "Framework", ".NETFramework");
        if (!Directory.Exists(root))
            return [];

        // Fall back to the newest installed reference assemblies when the exact version is
        // absent; they are backwards compatible and the alternative is offering nothing.
        string directory = Path.Combine(root, version);
        if (!Directory.Exists(directory))
        {
            directory = Directory.EnumerateDirectories(root)
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault() ?? "";
        }

        if (!Directory.Exists(directory))
            return [];

        return
        [
            .. Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => name is { Length: > 0 })
                .Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>
    /// Adds a plain <c>&lt;Reference&gt;</c> to a project — the .NET Framework equivalent of a
    /// package reference, and the one kind of dependency the tree could not add.
    /// </summary>
    public static async Task<MutationResult> AddAssemblyReferenceAsync(
        string projectPath, string assemblyName, CancellationToken ct = default)
    {
        if (!File.Exists(projectPath))
            return new MutationResult(false, $"Project not found: {projectPath}");
        if (string.IsNullOrWhiteSpace(assemblyName))
            return new MutationResult(false, "An assembly name is required.");

        try
        {
            var document = XDocument.Load(projectPath, LoadOptions.PreserveWhitespace);
            if (document.Root is not { } root)
                return new MutationResult(false, "The project file is empty.");

            var ns = root.Name.Namespace;

            bool already = root.Descendants(ns + "Reference").Any(r =>
                (r.Attribute("Include")?.Value ?? "")
                    .Split(',')[0]
                    .Trim()
                    .Equals(assemblyName, StringComparison.OrdinalIgnoreCase));
            if (already)
                return new MutationResult(true, $"{assemblyName} is already referenced.");

            // Beside the other assembly references when there are any, so the file keeps the
            // grouping its author gave it.
            var group = root.Descendants(ns + "Reference").FirstOrDefault()?.Parent
                ?? new XElement(ns + "ItemGroup");
            if (group.Parent is null)
                root.Add(group);

            group.Add(new XElement(ns + "Reference", new XAttribute("Include", assemblyName)));
            document.Save(projectPath);
        }
        catch (Exception ex)
        {
            return new MutationResult(false, $"Could not add the reference: {ex.Message}");
        }

        await InvalidateAsync(ct, projectPath);
        return new MutationResult(true, $"Added a reference to {assemblyName}.");
    }

    /// <summary>
    /// Makes sure a file that already exists on disk is one the project compiles.
    /// </summary>
    /// <remarks>
    /// Only legacy projects need this — an SDK-style project globs its sources — but the caller
    /// cannot tell them apart, and <see cref="EnsureCompileItem"/> already declines when the
    /// glob covers it.
    /// </remarks>
    public static async Task IncludeExistingFileAsync(
        string projectPath, string filePath, CancellationToken ct = default)
    {
        string projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        EnsureCompileItem(projectPath, projectDirectory, Path.GetFullPath(filePath));
        await InvalidateAsync(ct, projectPath);
    }

    /// <summary>
    /// Drops a file that has already been deleted from its project's item list.
    /// </summary>
    /// <remarks>
    /// <see cref="DeleteFileAsync"/> does the deleting itself and so refuses a path that is
    /// already gone. This is the other half: the editor deleted the file and only tells us
    /// afterwards, and a legacy project still carrying a <c>&lt;Compile&gt;</c> for it will not
    /// build.
    /// </remarks>
    public static async Task ForgetDeletedFileAsync(string filePath, CancellationToken ct = default)
    {
        string full = Path.GetFullPath(filePath);
        if (FindOwningProject(full) is { } owner)
        {
            RemoveCompileItem(owner, full);
            await InvalidateAsync(ct, owner);
        }
        else
        {
            await InvalidateAsync(ct);
        }
    }

    /// <summary>
    /// Points a project's items at a file's new path when that file is renamed or moved.
    /// </summary>
    /// <remarks>
    /// A legacy project lists every file explicitly, so a rename that only touches disk leaves an
    /// item naming a path that is gone and the project stops building. An SDK-style project globs
    /// instead and has no item to move, which is why finding nothing is success rather than an
    /// error — the caller cannot tell the two apart and should not have to.
    ///
    /// The <c>DependentUpon</c> metadata that nests <c>Default.aspx.cs</c> under
    /// <c>Default.aspx</c> names its parent relative to the item's own folder rather than the
    /// project's, so it is resolved to a path before being compared.
    /// </remarks>
    public static async Task RenameFileItemAsync(
        string oldPath, string newPath, CancellationToken ct = default)
    {
        string oldFull = Path.GetFullPath(oldPath);
        string newFull = Path.GetFullPath(newPath);
        if (oldFull.Equals(newFull, StringComparison.OrdinalIgnoreCase))
            return;

        string? owner = TryFindOwningProject(newFull) ?? TryFindOwningProject(oldFull);
        if (owner is null)
        {
            await InvalidateAsync(ct);
            return;
        }

        RewriteItemPath(owner, oldFull, newFull);

        // A move that crosses a project boundary leaves the file listed by a project that no
        // longer contains it, which breaks that project just as surely as the stale path would.
        if (TryFindOwningProject(oldFull) is { } previous &&
            !PathHelper.NormalizePath(previous).Equals(
                PathHelper.NormalizePath(owner), StringComparison.OrdinalIgnoreCase))
        {
            RemoveCompileItem(previous, oldFull);
            await InvalidateAsync(ct, previous);
        }

        await InvalidateAsync(ct, owner);
    }

    /// <summary>
    /// Points every explicit item at a file's new path, metadata included. When the thing that
    /// moved is a folder, every item beneath it moves with it — a legacy project lists each page
    /// individually, so a folder rename that only fixed the folder's own item would leave all of
    /// them naming a path that no longer exists.
    /// </summary>
    private static void RewriteItemPath(string projectPath, string oldFull, string newFull)
    {
        // The move has already happened on disk by the time this runs, so the old path is gone and
        // only the destination can answer what kind of thing it was.
        bool movedFolder = Directory.Exists(newFull);
        string oldPrefix = oldFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        try
        {
            // Nothing here rewrites structure, so the file keeps the formatting its author gave
            // it and the change reads as the one-line move it is.
            var document = XDocument.Load(projectPath, LoadOptions.PreserveWhitespace);
            if (document.Root is null)
                return;

            string projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
            string newInclude = Path.GetRelativePath(projectDirectory, newFull);
            bool changed = false;

            foreach (var item in document.Root.Descendants())
            {
                // Remove is in there because an excluded file that gets renamed would otherwise
                // fall back into the glob it was taken out of.
                var attribute = item.Attribute("Include")
                    ?? item.Attribute("Update")
                    ?? item.Attribute("Remove");
                if (attribute is null || !NamesOneFile(attribute.Value))
                    continue;

                string itemPath = ResolveAgainst(projectDirectory, attribute.Value);

                // DependentUpon is relative to the item's own folder, and for the item being
                // renamed that folder is the one it is moving to.
                string itemDirectory = Path.GetDirectoryName(itemPath)!;

                if (itemPath.Equals(oldFull, StringComparison.OrdinalIgnoreCase))
                {
                    attribute.Value = newInclude;
                    itemDirectory = Path.GetDirectoryName(newFull)!;
                    changed = true;
                }
                else if (movedFolder && itemPath.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    string moved = Path.Combine(newFull, itemPath[oldPrefix.Length..]);
                    attribute.Value = Path.GetRelativePath(projectDirectory, moved);
                    // DependentUpon is relative to the item's own folder, and the whole folder
                    // moved together, so those values stay correct — but the folder they resolve
                    // against is the new one.
                    itemDirectory = Path.GetDirectoryName(moved)!;
                    changed = true;
                }

                changed |= RewriteDependentUpon(item, itemDirectory, oldFull, newFull);
            }

            if (changed)
                document.Save(projectPath);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn(
                $"Could not move the item for '{Path.GetFileName(oldFull)}' in " +
                $"'{Path.GetFileName(projectPath)}': {ex.Message}",
                key: $"item-rename:{projectPath}");
        }
    }

    /// <returns>Whether the metadata was pointing at the renamed file and now points at it.</returns>
    private static bool RewriteDependentUpon(
        XElement item, string itemDirectory, string oldFull, string newFull)
    {
        var metadata = item.Element(item.Name.Namespace + "DependentUpon");
        string? value = metadata?.Value.Trim() ?? item.Attribute("DependentUpon")?.Value;
        if (value is null || !NamesOneFile(value))
            return false;

        if (!ResolveAgainst(itemDirectory, value).Equals(oldFull, StringComparison.OrdinalIgnoreCase))
            return false;

        string updated = Path.GetRelativePath(itemDirectory, newFull);
        if (metadata is not null)
            metadata.Value = updated;
        else
            item.SetAttributeValue("DependentUpon", updated);

        return true;
    }

    /// <summary>
    /// Whether an item's value is a plain path rather than a glob, a property reference or a
    /// list — the only form that can be compared against a file on disk without evaluating the
    /// project.
    /// </summary>
    private static bool NamesOneFile(string value) =>
        value.Length > 0 && value.IndexOfAny(['*', '?', '$', '%', '@', ';']) < 0;

    private static string ResolveAgainst(string directory, string relative) =>
        Path.GetFullPath(Path.Combine(
            directory,
            relative.Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>The owning project, or null when the folder went away with the file.</summary>
    private static string? TryFindOwningProject(string path)
    {
        try
        {
            return FindOwningProject(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static async Task<MutationResult> CreateProjectAsync(
        string template, string name, string directory, string? targetFramework = null,
        bool addToSolution = true, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new MutationResult(false, "A project name is required.");

        string outputDirectory = Path.GetFullPath(Path.Combine(directory, name));
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
            return new MutationResult(false, $"{outputDirectory} already exists and is not empty.");

        var arguments = new List<string> { "new", template, "-n", name, "-o", outputDirectory };
        if (!string.IsNullOrWhiteSpace(targetFramework))
            arguments.AddRange(["-f", targetFramework]);

        var (exitCode, output) = await RunDotnetAsync(arguments, ct);
        if (exitCode != 0)
            return new MutationResult(false, FirstError(output));

        string? created = Directory
            .EnumerateFiles(outputDirectory, "*.*proj", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();

        var message = new StringBuilder($"Created {name} from the '{template}' template.");

        if (addToSolution && created is not null &&
            WorkspaceService.TryGetMostRecentSolution()?.FilePath is { Length: > 0 } solution)
        {
            var added = await AddProjectToSolutionAsync(created, solution, ct);
            message.Append(' ').Append(added.Message);
        }

        await InvalidateAsync(ct);
        return new MutationResult(true, message.ToString());
    }

    public static async Task<MutationResult> AddProjectToSolutionAsync(
        string projectPath, string? solutionPath = null, CancellationToken ct = default)
    {
        solutionPath ??= WorkspaceService.TryGetMostRecentSolution()?.FilePath;
        if (string.IsNullOrEmpty(solutionPath) || !File.Exists(solutionPath))
            return new MutationResult(false, "No solution is open.");
        if (!File.Exists(projectPath))
            return new MutationResult(false, $"Project not found: {projectPath}");

        var (exitCode, output) = await RunDotnetAsync(
            ["sln", solutionPath, "add", projectPath], ct);

        if (exitCode != 0)
            return new MutationResult(false, FirstError(output));

        await InvalidateAsync(ct);
        return new MutationResult(true,
            $"Added {Path.GetFileNameWithoutExtension(projectPath)} to " +
            $"{Path.GetFileName(solutionPath)}.");
    }

    public static async Task<MutationResult> RemoveProjectFromSolutionAsync(
        string projectPath, string? solutionPath = null, CancellationToken ct = default)
    {
        solutionPath ??= WorkspaceService.TryGetMostRecentSolution()?.FilePath;
        if (string.IsNullOrEmpty(solutionPath) || !File.Exists(solutionPath))
            return new MutationResult(false, "No solution is open.");

        var (exitCode, output) = await RunDotnetAsync(
            ["sln", solutionPath, "remove", projectPath], ct);

        if (exitCode != 0)
            return new MutationResult(false, FirstError(output));

        await InvalidateAsync(ct);
        return new MutationResult(true,
            $"Removed {Path.GetFileNameWithoutExtension(projectPath)} from " +
            $"{Path.GetFileName(solutionPath)}. The files are still on disk.");
    }

    /// <summary>
    /// Drops a file from its project's item list without deleting it — Visual Studio's
    /// "Exclude From Project".
    /// </summary>
    /// <remarks>
    /// The two project styles need opposite edits. A legacy project lists every file, so removing
    /// the item is enough. An SDK-style project globs them, so there is no item to remove and the
    /// exclusion has to be stated: a <c>Remove</c> item that subtracts the file from the glob.
    /// </remarks>
    public static async Task<MutationResult> ExcludeFileAsync(
        string projectPath, string filePath, CancellationToken ct = default)
    {
        string full = Path.GetFullPath(filePath);
        string projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        string include = Path.GetRelativePath(projectDirectory, full);

        try
        {
            var document = XDocument.Load(projectPath);
            if (document.Root is null)
                return new MutationResult(false, "The project file could not be read.");

            RemoveCompileItem(projectPath, full);

            bool sdkStyle = document.Root.Attribute("Sdk") is not null ||
                            document.Root.Elements().Any(e => e.Name.LocalName == "Sdk");
            string? enableDefaults = ReadProperty(projectPath, "EnableDefaultCompileItems");
            bool globbed = sdkStyle &&
                !string.Equals(enableDefaults, "false", StringComparison.OrdinalIgnoreCase);

            if (globbed)
            {
                // Reloaded: RemoveCompileItem may have written the file out from under us.
                document = XDocument.Load(projectPath);
                var ns = document.Root!.Name.Namespace;

                var missing = DefaultGlobsFor(full)
                    .Where(item => !document.Descendants(ns + item).Any(e => string.Equals(
                        e.Attribute("Remove")?.Value, include, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                if (missing.Count > 0)
                {
                    document.Root.Add(new XElement(ns + "ItemGroup",
                        missing.Select(item =>
                            new XElement(ns + item, new XAttribute("Remove", include)))));
                    document.Save(projectPath);
                }
            }
        }
        catch (Exception ex)
        {
            return new MutationResult(false, $"Could not edit the project file: {ex.Message}");
        }

        await InvalidateAsync(ct, projectPath);
        return new MutationResult(true,
            $"Excluded {Path.GetFileName(full)} from " +
            $"{Path.GetFileNameWithoutExtension(projectPath)}. The file is still on disk.");
    }

    /// <summary>
    /// The item types whose default glob would pick a file up, and which therefore have to be
    /// told not to.
    /// </summary>
    /// <remarks>
    /// Which glob claims a file depends on the SDK: the base SDK globs <c>None</c> for anything
    /// it does not compile, while the Web SDK globs <c>Content</c> for the same files. Rather
    /// than work out which SDK is in play, both are excluded — a <c>Remove</c> for an item type
    /// nothing matched costs nothing, and guessing wrong leaves the file in the build.
    /// </remarks>
    private static string[] DefaultGlobsFor(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cs" or ".vb" or ".fs" => ["Compile"],
            ".resx" => ["EmbeddedResource"],
            _ => ["None", "Content"],
        };

    // --- File scaffolding ---

    private static string Scaffold(
        string projectPath, string projectDirectory, string fullPath, FileKind kind)
    {
        if (kind == FileKind.Empty)
            return "";

        string typeName = Path.GetFileNameWithoutExtension(fullPath);
        string @namespace = InferNamespace(projectPath, projectDirectory, fullPath);

        string keyword = kind switch
        {
            FileKind.Interface => "interface",
            FileKind.Record => "record",
            FileKind.Enum => "enum",
            _ => "class",
        };

        // An interface named Foo is almost never wanted; IFoo is.
        if (kind == FileKind.Interface && !typeName.StartsWith('I'))
            typeName = "I" + typeName;

        var sb = new StringBuilder();
        if (@namespace.Length > 0)
        {
            sb.AppendLine($"namespace {@namespace};");
            sb.AppendLine();
        }
        sb.AppendLine($"public {keyword} {typeName}");
        sb.AppendLine("{");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// Root namespace plus the folders between the project and the file — the correspondence
    /// every C# codebase assumes and every analyzer checks.
    /// </summary>
    private static string InferNamespace(string projectPath, string projectDirectory, string fullPath)
    {
        string root = ReadProperty(projectPath, "RootNamespace")
            ?? Path.GetFileNameWithoutExtension(projectPath);

        string? folders = Path.GetDirectoryName(Path.GetRelativePath(projectDirectory, fullPath));
        if (string.IsNullOrEmpty(folders) || folders == ".")
            return Sanitize(root);

        var parts = folders
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Where(p => p.Length > 0 && p != ".");

        return Sanitize(root + "." + string.Join('.', parts));
    }

    private static string Sanitize(string @namespace)
    {
        var sb = new StringBuilder(@namespace.Length);
        foreach (char ch in @namespace)
            sb.Append(char.IsLetterOrDigit(ch) || ch is '.' or '_' ? ch : '_');

        // A namespace part cannot start with a digit.
        return string.Join('.', sb.ToString()
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.IsAsciiDigit(part[0]) ? "_" + part : part));
    }

    private static string? ReadProperty(string projectPath, string name)
    {
        try
        {
            return XDocument.Load(projectPath)
                .Descendants()
                .FirstOrDefault(e => e.Name.LocalName == name)
                ?.Value.Trim() is { Length: > 0 } value
                ? value
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Adds a <c>Compile</c> item when the project does not glob its sources.
    /// </summary>
    /// <returns>What was written, or <c>null</c> when the SDK's default glob already covers it.</returns>
    private static string? EnsureCompileItem(string projectPath, string projectDirectory, string fullPath)
    {
        try
        {
            var document = XDocument.Load(projectPath);
            if (document.Root is null)
                return null;

            // SDK-style projects include **/*.cs unless the author opted out.
            bool sdkStyle = document.Root.Attribute("Sdk") is not null ||
                            document.Root.Elements().Any(e => e.Name.LocalName == "Sdk");
            string? enableDefaults = ReadProperty(projectPath, "EnableDefaultCompileItems");
            if (sdkStyle && !string.Equals(enableDefaults, "false", StringComparison.OrdinalIgnoreCase))
                return null;

            if (!fullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return null;

            var ns = document.Root.Name.Namespace;
            string include = Path.GetRelativePath(projectDirectory, fullPath);

            var group = document.Root.Elements(ns + "ItemGroup")
                .FirstOrDefault(g => g.Elements(ns + "Compile").Any());
            if (group is null)
            {
                group = new XElement(ns + "ItemGroup");
                document.Root.Add(group);
            }

            group.Add(new XElement(ns + "Compile", new XAttribute("Include", include)));
            document.Save(projectPath);
            return "added it to the project file";
        }
        catch (Exception ex)
        {
            ServiceLog.Warn(
                $"Could not add a Compile item to '{Path.GetFileName(projectPath)}': {ex.Message}",
                key: $"compile-item:{projectPath}");
            return null;
        }
    }

    private static void RemoveCompileItem(string projectPath, string fullPath)
    {
        try
        {
            var document = XDocument.Load(projectPath);
            if (document.Root is null)
                return;

            string projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
            string include = Path.GetRelativePath(projectDirectory, fullPath);

            var stale = document.Descendants()
                .Where(e => e.Name.LocalName is "Compile" or "None" or "Content" or "EmbeddedResource")
                .Where(e => string.Equals(
                    e.Attribute("Include")?.Value ?? e.Attribute("Update")?.Value,
                    include, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (stale.Count == 0)
                return;

            foreach (var element in stale)
                element.Remove();
            document.Save(projectPath);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn(
                $"Could not remove the item for '{Path.GetFileName(fullPath)}': {ex.Message}",
                key: $"compile-item-remove:{projectPath}");
        }
    }

    /// <summary>The nearest project above a file, which owns it for item purposes.</summary>
    private static string? FindOwningProject(string filePath)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(filePath)!);
        while (directory is not null)
        {
            string? project = Directory
                .EnumerateFiles(directory.FullName, "*.*proj", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(p => !p.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase));
            if (project is not null)
                return project;
            directory = directory.Parent;
        }
        return null;
    }

    private static async Task<bool> ReferencesTransitivelyAsync(
        string from, string target, CancellationToken ct)
    {
        string goal = PathHelper.NormalizePath(target);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>([from]);

        while (queue.Count > 0)
        {
            string current = queue.Dequeue();
            if (!seen.Add(PathHelper.NormalizePath(current)))
                continue;

            var evaluation = await ProjectEvaluationService.EvaluateAsync(current, ct);
            foreach (string reference in evaluation?.ProjectReferences ?? [])
            {
                if (PathHelper.NormalizePath(reference).Equals(goal, StringComparison.OrdinalIgnoreCase))
                    return true;
                queue.Enqueue(reference);
            }
        }
        return false;
    }

    private static async Task InvalidateAsync(CancellationToken ct, params string[] projects)
    {
        foreach (string project in projects)
            ProjectEvaluationService.Evict(project);

        // The compilation itself changed, so every cached snapshot and analyzer result is stale.
        await WorkspaceService.EvictAllAsync(ct);
    }

    private static async Task<(int ExitCode, string Output)> RunDotnetAsync(
        IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo);
        if (process is null)
            return (-1, "Failed to start dotnet.");

        string stdout = await process.StandardOutput.ReadToEndAsync(ct);
        string stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return (process.ExitCode, stderr.Length > 0 ? stderr : stdout);
    }

    private static string FirstError(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.Contains("error", StringComparison.OrdinalIgnoreCase))
        ?? output.Split('\n').FirstOrDefault()?.Trim()
        ?? "The command failed.";
}

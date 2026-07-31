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

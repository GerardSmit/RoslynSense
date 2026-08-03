using System.Xml.Linq;
using Microsoft.Build.Construction;

namespace RoslynMCP.Services.ProjectModel;

/// <summary>A node in the solution's logical tree: a solution folder or a project.</summary>
public sealed record SolutionNode(
    string Id,
    string? ParentId,
    string Name,
    string? Path,
    bool IsFolder,
    IReadOnlyList<string> Files);

/// <summary>
/// The solution's *logical* structure — the folder hierarchy authors see in Visual Studio and
/// Rider. Roslyn's Solution model has no concept of solution folders, so this reads the file
/// itself: `.sln` through MSBuild's own parser (which resolves the NestedProjects section),
/// `.slnx` through its XML.
/// </summary>
public static class SolutionFileService
{
    public static IReadOnlyList<SolutionNode> Read(string solutionPath)
    {
        if (!File.Exists(solutionPath))
            return [];

        try
        {
            if (solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                return ReadSlnx(solutionPath);

            // Microsoft.Build ships with runtime assets excluded, so SolutionFile resolves only
            // through MSBuildLocator's resolver — touching it before registration takes down the
            // process rather than throwing.
            WorkspaceService.EnsureRegistered();
            return ReadSln(solutionPath);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn(
                $"Could not read the structure of '{Path.GetFileName(solutionPath)}': {ex.Message}",
                key: $"solution-parse:{solutionPath}");
            return [];
        }
    }

    // NoInlining: the JIT resolves a method's types on entry, so inlining this would load
    // Microsoft.Build before EnsureRegistered() had run.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static IReadOnlyList<SolutionNode> ReadSln(string solutionPath)
    {
        var solution = SolutionFile.Parse(solutionPath);
        var nodes = new List<SolutionNode>();

        foreach (var project in solution.ProjectsInOrder)
        {
            bool isFolder = project.ProjectType == SolutionProjectType.SolutionFolder;

            // A solution folder's "path" is its own name; only real projects have a file.
            string? path = isFolder
                ? null
                : Path.GetFullPath(project.AbsolutePath);

            nodes.Add(new SolutionNode(
                Id: project.ProjectGuid,
                ParentId: string.IsNullOrEmpty(project.ParentProjectGuid) ? null : project.ParentProjectGuid,
                Name: project.ProjectName,
                Path: path,
                IsFolder: isFolder,
                Files: isFolder ? ReadFolderFiles(solutionPath, project) : []));
        }

        return nodes;
    }

    /// <summary>Files attached to a solution folder ("Solution Items"). MSBuild's parser does
    /// not expose them, so they come from the raw section.</summary>
    private static IReadOnlyList<string> ReadFolderFiles(
        string solutionPath, ProjectInSolution folder)
    {
        var files = new List<string>();
        string? solutionDir = Path.GetDirectoryName(solutionPath);
        if (solutionDir is null)
            return files;

        bool inFolder = false;
        bool inSection = false;

        foreach (string raw in File.ReadLines(solutionPath))
        {
            string line = raw.Trim();

            if (line.StartsWith("Project(", StringComparison.Ordinal))
            {
                inFolder = line.Contains(folder.ProjectGuid, StringComparison.OrdinalIgnoreCase);
                inSection = false;
                continue;
            }
            if (!inFolder)
                continue;

            if (line.StartsWith("ProjectSection(SolutionItems)", StringComparison.Ordinal))
            {
                inSection = true;
                continue;
            }
            if (line.StartsWith("EndProjectSection", StringComparison.Ordinal) ||
                line.StartsWith("EndProject", StringComparison.Ordinal))
            {
                inSection = false;
                continue;
            }

            if (!inSection)
                continue;

            // Entries read "relative\path = relative\path".
            int equals = line.IndexOf('=');
            string relative = (equals > 0 ? line[..equals] : line).Trim();
            if (relative.Length > 0)
                files.Add(Path.GetFullPath(Path.Combine(solutionDir, relative)));
        }

        return files;
    }

    private static IReadOnlyList<SolutionNode> ReadSlnx(string solutionPath)
    {
        string solutionDir = Path.GetDirectoryName(solutionPath)!;
        var document = XDocument.Load(solutionPath);
        var nodes = new List<SolutionNode>();

        void Walk(XElement element, string? parentId)
        {
            foreach (var child in element.Elements())
            {
                if (child.Name.LocalName.Equals("Folder", StringComparison.OrdinalIgnoreCase))
                {
                    string name = (child.Attribute("Name")?.Value ?? "").Trim('/', '\\');
                    string id = parentId is null ? $"/{name}" : $"{parentId}/{name}";

                    // Solution items: what the .sln format calls ProjectSection(SolutionItems),
                    // written as <File Path="..."/> children here.
                    var files = child.Elements()
                        .Where(e => e.Name.LocalName.Equals("File", StringComparison.OrdinalIgnoreCase))
                        .Select(e => e.Attribute("Path")?.Value)
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                        .Select(path => Path.GetFullPath(Path.Combine(solutionDir, path!)))
                        .ToList();

                    nodes.Add(new SolutionNode(id, parentId, name, null, IsFolder: true, Files: files));
                    Walk(child, id);
                }
                else if (child.Name.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase) &&
                         child.Attribute("Path")?.Value is { Length: > 0 } relative)
                {
                    string full = Path.GetFullPath(Path.Combine(solutionDir, relative));
                    nodes.Add(new SolutionNode(
                        Id: full,
                        ParentId: parentId,
                        Name: Path.GetFileNameWithoutExtension(full),
                        Path: full,
                        IsFolder: false,
                        Files: []));
                }
            }
        }

        if (document.Root is { } root)
            Walk(root, null);

        return nodes;
    }
}

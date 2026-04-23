using System.ComponentModel;
using System.Text;
using System.Xml.Linq;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

[McpServerToolType]
public static class ListProjectsTool
{
    [McpServerTool, Description(
        "List all projects in a solution or directory. Discovers .sln/.slnx files and enumerates " +
        "their projects, or searches a directory for .csproj files.")]
    public static async Task<string> ListProjects(
        [Description(
            "Path to a .sln/.slnx file, .csproj file, any source file, or a directory to search for projects.")]
        string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return "Error: Path cannot be empty.";

            string systemPath = PathHelper.NormalizePath(path);

            // If it's a solution file, parse it for project entries
            if (PathHelper.IsSolutionFile(systemPath) && File.Exists(systemPath))
            {
                if (systemPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                    return FormatSlnxProjects(systemPath);
                return await FormatSolutionProjectsAsync(systemPath, cancellationToken);
            }

            // If it's a file or directory, walk up to find the nearest .sln or .csproj
            if (File.Exists(systemPath) || Directory.Exists(systemPath))
            {
                // Try to find a .sln by walking up the directory tree
                var slnPath = PathHelper.FindNearestSolution(systemPath);
                if (slnPath is not null)
                    return await FormatSolutionProjectsAsync(slnPath, cancellationToken);

                // No .sln found — try to find a .csproj by walking up
                var csprojPath = PathHelper.ResolveCsprojPath(systemPath);
                if (csprojPath is not null)
                {
                    var projectDir = Path.GetDirectoryName(csprojPath)!;
                    return FormatDiscoveredProjects(projectDir);
                }

                // Fallback: list .csproj files under the given path (if directory)
                var searchDir = File.Exists(systemPath) ? Path.GetDirectoryName(systemPath)! : systemPath;
                return FormatDiscoveredProjects(searchDir);
            }

            return $"Error: Path '{path}' does not exist.";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ListProjects] Unhandled error: {ex}");
            return $"Error: {ex.Message}";
        }
    }

    private static async Task<string> FormatSolutionProjectsAsync(
        string slnPath, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        var slnDir = Path.GetDirectoryName(slnPath)!;
        sb.AppendLine($"# Solution: {Path.GetFileName(slnPath)}");
        sb.AppendLine();

        var lines = await File.ReadAllLinesAsync(slnPath, cancellationToken);
        var projects = new List<(string Name, string RelativePath, string Type)>();

        foreach (var line in lines)
        {
            // Format: Project("{FAE04EC0-...}") = "Name", "Path\To\Project.csproj", "{GUID}"
            if (!line.StartsWith("Project(", StringComparison.Ordinal)) continue;

            var parts = line.Split('"');
            if (parts.Length < 6) continue;

            string name = parts[3];
            string relativePath = parts[5];

            // Skip solution folders
            if (!relativePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
                !relativePath.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase) &&
                !relativePath.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase))
                continue;

            string fullPath = Path.GetFullPath(Path.Combine(slnDir, relativePath.Replace('\\', Path.DirectorySeparatorChar)));
            string type = DetectProjectType(fullPath);

            projects.Add((name, relativePath.Replace('\\', '/'), type));
        }

        if (projects.Count == 0)
        {
            sb.AppendLine("No projects found in the solution.");
            return sb.ToString();
        }

        sb.AppendLine($"Found **{projects.Count}** project(s):");
        sb.AppendLine();
        sb.AppendLine("| # | Project | Path | Type |");
        sb.AppendLine("|---|---------|------|------|");

        int index = 1;
        foreach (var (name, relativePath, type) in projects)
        {
            sb.AppendLine($"| {index} | {name} | {relativePath} | {type} |");
            index++;
        }

        return sb.ToString();
    }

    private static string FormatDiscoveredProjects(string directory)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Projects in: {directory}");
        sb.AppendLine();

        var csprojFiles = Directory.GetFiles(directory, "*.csproj", SearchOption.AllDirectories)
            .Where(f =>
            {
                var rel = Path.GetRelativePath(directory, f);
                var first = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                return !first.Equals("bin", StringComparison.OrdinalIgnoreCase) &&
                       !first.Equals("obj", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(f => f)
            .ToList();

        if (csprojFiles.Count == 0)
        {
            sb.AppendLine("No .csproj files found.");
            return sb.ToString();
        }

        sb.AppendLine($"Found **{csprojFiles.Count}** project(s):");
        sb.AppendLine();
        sb.AppendLine("| # | Project | Path | Type |");
        sb.AppendLine("|---|---------|------|------|");

        int index = 1;
        foreach (var file in csprojFiles)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            string relativePath = Path.GetRelativePath(directory, file).Replace('\\', '/');
            string type = DetectProjectType(file);
            sb.AppendLine($"| {index} | {name} | {relativePath} | {type} |");
            index++;
        }

        return sb.ToString();
    }

    private static string FormatSlnxProjects(string slnxPath)
    {
        var sb = new StringBuilder();
        var slnDir = Path.GetDirectoryName(slnxPath)!;
        sb.AppendLine($"# Solution: {Path.GetFileName(slnxPath)}");
        sb.AppendLine();

        var doc = XDocument.Load(slnxPath);
        var projects = new List<(string Name, string RelativePath, string Type)>();

        foreach (var elem in doc.Descendants("Project"))
        {
            var pathAttr = elem.Attribute("Path")?.Value;
            if (string.IsNullOrEmpty(pathAttr)) continue;

            if (!pathAttr.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
                !pathAttr.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase) &&
                !pathAttr.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase))
                continue;

            string fullPath = Path.GetFullPath(Path.Combine(slnDir, pathAttr.Replace('/', Path.DirectorySeparatorChar)));
            string name = Path.GetFileNameWithoutExtension(pathAttr);
            string type = DetectProjectType(fullPath);
            projects.Add((name, pathAttr.Replace('\\', '/'), type));
        }

        if (projects.Count == 0)
        {
            sb.AppendLine("No projects found in the solution.");
            return sb.ToString();
        }

        sb.AppendLine($"Found **{projects.Count}** project(s):");
        sb.AppendLine();
        sb.AppendLine("| # | Project | Path | Type |");
        sb.AppendLine("|---|---------|------|------|");

        int index = 1;
        foreach (var (name, relativePath, type) in projects)
        {
            sb.AppendLine($"| {index} | {name} | {relativePath} | {type} |");
            index++;
        }

        return sb.ToString();
    }

    private static string DetectProjectType(string csprojPath)
    {
        if (!File.Exists(csprojPath)) return "?";

        try
        {
            var content = File.ReadAllText(csprojPath);
            bool isTest = content.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase) ||
                          content.Contains("xunit", StringComparison.OrdinalIgnoreCase) ||
                          content.Contains("NUnit", StringComparison.OrdinalIgnoreCase) ||
                          content.Contains("MSTest", StringComparison.OrdinalIgnoreCase);

            bool isExe = content.Contains("<OutputType>Exe</OutputType>", StringComparison.OrdinalIgnoreCase);
            bool isTool = content.Contains("<PackAsTool>true</PackAsTool>", StringComparison.OrdinalIgnoreCase);

            if (isTest) return "Test";
            if (isTool) return "Tool";
            if (isExe) return "Exe";
            return "Library";
        }
        catch
        {
            return "?";
        }
    }
}

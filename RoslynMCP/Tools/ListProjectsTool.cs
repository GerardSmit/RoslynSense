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
        IOutputFormatter fmt,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return "Error: Path cannot be empty.";

            string systemPath = PathHelper.NormalizePath(path);

            // If it's a solution file, parse it for project entries
            if (PathHelper.IsSolutionFile(systemPath) && File.Exists(systemPath))
                return FormatProjects(fmt, Path.GetFileName(systemPath),
                    await ParseSolutionAsync(systemPath, cancellationToken));

            // If it's a file or directory, walk up to find the nearest .sln or .csproj
            if (File.Exists(systemPath) || Directory.Exists(systemPath))
            {
                // Try to find a solution by walking up the directory tree
                var slnPath = PathHelper.FindNearestSolution(systemPath);
                if (slnPath is not null)
                    return FormatProjects(fmt, Path.GetFileName(slnPath),
                        await ParseSolutionAsync(slnPath, cancellationToken));

                // No solution found — try to find a .csproj by walking up
                var csprojPath = PathHelper.ResolveCsprojPath(systemPath);
                if (csprojPath is not null)
                {
                    var projectDir = Path.GetDirectoryName(csprojPath)!;
                    return FormatProjects(fmt, projectDir, DiscoverProjects(projectDir));
                }

                // Fallback: list .csproj files under the given path (if directory)
                var searchDir = File.Exists(systemPath) ? Path.GetDirectoryName(systemPath)! : systemPath;
                return FormatProjects(fmt, searchDir, DiscoverProjects(searchDir));
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

    private static string FormatProjects(
        IOutputFormatter fmt, string label, List<(string Name, string RelativePath, string Type)> projects)
    {
        var sb = new StringBuilder();
        fmt.AppendHeader(sb, $"Solution: {label}");

        if (projects.Count == 0)
        {
            fmt.AppendEmpty(sb, "No projects found.");
            return sb.ToString();
        }

        var columns = new[] { "#", "Project", "Path", "Type" };
        var rows = new List<string[]>();
        for (int i = 0; i < projects.Count; i++)
        {
            var (name, relativePath, type) = projects[i];
            rows.Add([(i + 1).ToString(), name, relativePath, type]);
        }

        fmt.AppendTable(sb, "Projects", columns, rows);
        return sb.ToString();
    }

    private static async Task<List<(string Name, string RelativePath, string Type)>> ParseSolutionAsync(
        string solutionPath, CancellationToken cancellationToken)
    {
        if (solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            return ParseSlnx(solutionPath);
        return await ParseSlnAsync(solutionPath, cancellationToken);
    }

    private static async Task<List<(string Name, string RelativePath, string Type)>> ParseSlnAsync(
        string slnPath, CancellationToken cancellationToken)
    {
        var slnDir = Path.GetDirectoryName(slnPath)!;
        var projects = new List<(string Name, string RelativePath, string Type)>();

        var lines = await File.ReadAllLinesAsync(slnPath, cancellationToken);
        foreach (var line in lines)
        {
            if (!line.StartsWith("Project(", StringComparison.Ordinal)) continue;

            var parts = line.Split('"');
            if (parts.Length < 6) continue;

            string name = parts[3];
            string relativePath = parts[5];

            if (!IsProjectFile(relativePath)) continue;

            string fullPath = Path.GetFullPath(Path.Combine(slnDir, relativePath.Replace('\\', Path.DirectorySeparatorChar)));
            projects.Add((name, relativePath.Replace('\\', '/'), DetectProjectType(fullPath)));
        }

        return projects;
    }

    private static List<(string Name, string RelativePath, string Type)> ParseSlnx(string slnxPath)
    {
        var slnDir = Path.GetDirectoryName(slnxPath)!;
        var projects = new List<(string Name, string RelativePath, string Type)>();
        var doc = XDocument.Load(slnxPath);

        foreach (var elem in doc.Descendants("Project"))
        {
            var pathAttr = elem.Attribute("Path")?.Value;
            if (string.IsNullOrEmpty(pathAttr) || !IsProjectFile(pathAttr)) continue;

            string fullPath = Path.GetFullPath(Path.Combine(slnDir, pathAttr.Replace('/', Path.DirectorySeparatorChar)));
            string name = Path.GetFileNameWithoutExtension(pathAttr);
            projects.Add((name, pathAttr.Replace('\\', '/'), DetectProjectType(fullPath)));
        }

        return projects;
    }

    private static List<(string Name, string RelativePath, string Type)> DiscoverProjects(string directory)
    {
        var projects = new List<(string Name, string RelativePath, string Type)>();

        var csprojFiles = Directory.GetFiles(directory, "*.csproj", SearchOption.AllDirectories)
            .Where(f =>
            {
                var rel = Path.GetRelativePath(directory, f);
                var first = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                return !first.Equals("bin", StringComparison.OrdinalIgnoreCase) &&
                       !first.Equals("obj", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(f => f);

        foreach (var file in csprojFiles)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            string relativePath = Path.GetRelativePath(directory, file).Replace('\\', '/');
            projects.Add((name, relativePath, DetectProjectType(file)));
        }

        return projects;
    }

    private static bool IsProjectFile(string path) =>
        path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase);

    private static string DetectProjectType(string csprojPath)
    {
        if (!File.Exists(csprojPath)) return "?";

        var classification = ProjectClassifier.Classify(csprojPath);
        if (classification.IsTestProject) return "Test";

        // PackAsTool is packaging metadata rather than a project shape, so it stays a local read.
        try
        {
            if (File.ReadAllText(csprojPath)
                .Contains("<PackAsTool>true</PackAsTool>", StringComparison.OrdinalIgnoreCase))
                return "Tool";
        }
        catch
        {
            // Unreadable file: fall through to whatever the classifier managed to determine.
        }

        return classification.Kind switch
        {
            AppKind.AspNetCore => "Web",
            AppKind.AspNetClassic => "Web (IIS Express)",
            AppKind.ConsoleApp => "Exe",
            AppKind.WindowsApp => "WinExe",
            AppKind.ClassLibrary => "Library",
            _ => "Library",
        };
    }
}

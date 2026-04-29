using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace RoslynMCP.Services;

/// <summary>
/// Centralizes file-path normalization used by every MCP tool.
/// </summary>
internal static class PathHelper
{
    /// <summary>
    /// Reads the SDK attribute from a .csproj file (e.g., "Microsoft.NET.Sdk.Web").
    /// Returns null if the file is legacy (non-SDK-style) or cannot be read.
    /// </summary>
    public static string? ReadProjectSdk(string projectPath)
    {
        if (!projectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            using var reader = new StreamReader(projectPath);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                line = line.TrimStart();
                if (line.StartsWith("<Project", StringComparison.OrdinalIgnoreCase))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(
                        line, """Sdk\s*=\s*["']([^"']+)["']""",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (match.Success)
                        return match.Groups[1].Value;
                    break;
                }

                // Also check for <Sdk Name="..."/> import style
                if (line.StartsWith("<Sdk", StringComparison.OrdinalIgnoreCase))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(
                        line, """Name\s*=\s*["']([^"']+)["']""",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (match.Success)
                        return match.Groups[1].Value;
                }
            }
        }
        catch
        {
            // Don't fail if we can't read the project file
        }

        return null;
    }

    /// <summary>
    /// Returns true if a .csproj is a legacy (non-SDK-style) project.
    /// </summary>
    public static bool IsLegacyProject(string csprojPath) =>
        ReadProjectSdk(csprojPath) is null;

    /// <summary>
    /// Returns true if a .sln contains at least one legacy .csproj.
    /// </summary>
    public static bool IsLegacySolution(string slnPath)
    {
        if (!IsSolutionFile(slnPath) || !File.Exists(slnPath))
            return false;

        var slnDir = Path.GetDirectoryName(slnPath)!;

        try
        {
            if (slnPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                return IsLegacySolutionXml(slnPath, slnDir);

            // Parse Project("{type}") = "Name", "relative\path.csproj", "{GUID}" lines
            var projectLineRegex = new System.Text.RegularExpressions.Regex(
                @"Project\(""[^""]*""\)\s*=\s*""[^""]*""\s*,\s*""([^""]+\.(?:csproj|vbproj|fsproj))""",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            foreach (var line in File.ReadLines(slnPath))
            {
                var match = projectLineRegex.Match(line);
                if (!match.Success) continue;

                var relativePath = match.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar);
                var fullPath = Path.GetFullPath(Path.Combine(slnDir, relativePath));
                if (File.Exists(fullPath) && IsLegacyProject(fullPath))
                    return true;
            }
        }
        catch
        {
            // Don't fail if we can't read the solution
        }

        return false;
    }

    private static bool IsLegacySolutionXml(string slnxPath, string slnDir)
    {
        var doc = System.Xml.Linq.XDocument.Load(slnxPath);
        var projectElements = doc.Descendants("Project");

        foreach (var proj in projectElements)
        {
            var pathAttr = proj.Attribute("Path")?.Value;
            if (string.IsNullOrEmpty(pathAttr)) continue;
            if (!pathAttr.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
                !pathAttr.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase) &&
                !pathAttr.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase))
                continue;

            var fullPath = Path.GetFullPath(Path.Combine(slnDir, pathAttr.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(fullPath) && IsLegacyProject(fullPath))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true if the build target (a .csproj or .sln) requires MSBuild
    /// rather than the dotnet CLI.
    /// </summary>
    public static bool RequiresMsBuild(string buildTarget)
    {
        if (IsSolutionFile(buildTarget))
            return IsLegacySolution(buildTarget);
        if (buildTarget.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return IsLegacyProject(buildTarget);
        return false;
    }


    /// <summary>
    /// Normalizes a file path by resolving it to a full absolute path.
    /// </summary>
    public static string NormalizePath(string filePath) =>
        Path.GetFullPath(filePath);

    /// <summary>
    /// Returns true if the path ends with .sln or .slnx.
    /// </summary>
    public static bool IsSolutionFile(string path) =>
        path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Finds all solution files (.sln and .slnx) in a directory (non-recursive).
    /// </summary>
    public static string[] FindSolutionFiles(string directory)
    {
        var sln = Directory.GetFiles(directory, "*.sln");
        var slnx = Directory.GetFiles(directory, "*.slnx");
        if (sln.Length == 0) return slnx;
        if (slnx.Length == 0) return sln;
        return [.. sln, .. slnx];
    }

    /// <summary>
    /// Walks up from a file or directory looking for the nearest .sln.
    /// Returns null if not found.
    /// </summary>
    public static string? FindNearestSolution(string path)
    {
        var normalized = NormalizePath(path);
        var dir = File.Exists(normalized) ? Path.GetDirectoryName(normalized) : normalized;

        while (dir is not null)
        {
            var slnFiles = FindSolutionFiles(dir);
            if (slnFiles.Length >= 1) return slnFiles[0];
            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    /// <summary>
    /// Parses a .sln or .slnx file and returns the absolute paths of all .csproj projects it references.
    /// Returns an empty list if the file cannot be read or contains no C# projects.
    /// </summary>
    public static List<string> GetProjectsFromSolution(string solutionPath)
    {
        var slnDir = Path.GetDirectoryName(solutionPath)!;
        var result = new List<string>();
        try
        {
            if (solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                var doc = XDocument.Load(solutionPath);
                foreach (var elem in doc.Descendants("Project"))
                {
                    var rel = elem.Attribute("Path")?.Value;
                    if (!string.IsNullOrEmpty(rel) && rel.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                        result.Add(Path.GetFullPath(Path.Combine(slnDir, rel.Replace('/', Path.DirectorySeparatorChar))));
                }
            }
            else
            {
                foreach (var line in File.ReadAllLines(solutionPath))
                {
                    if (!line.StartsWith("Project(", StringComparison.Ordinal)) continue;
                    var parts = line.Split('"');
                    if (parts.Length < 6) continue;
                    var rel = parts[5];
                    if (rel.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                        result.Add(Path.GetFullPath(Path.Combine(slnDir, rel.Replace('\\', Path.DirectorySeparatorChar))));
                }
            }
        }
        catch { }
        return result;
    }

    /// <summary>
    /// Resolves a project path, file path, or directory to the containing .csproj file.
    /// Walks up directories from source files. Returns null if not found.
    /// </summary>
    public static string? ResolveCsprojPath(string projectPath)
    {
        var normalized = NormalizePath(projectPath);

        if (normalized.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && File.Exists(normalized))
            return normalized;

        if (File.Exists(normalized))
        {
            var dir = Path.GetDirectoryName(normalized);
            while (dir is not null)
            {
                var csprojs = Directory.GetFiles(dir, "*.csproj");
                if (csprojs.Length >= 1) return csprojs[0];
                dir = Path.GetDirectoryName(dir);
            }
        }

        if (Directory.Exists(normalized))
        {
            var csprojs = Directory.GetFiles(normalized, "*.csproj");
            if (csprojs.Length >= 1) return csprojs[0];
        }

        return null;
    }

    /// <summary>
    /// Returns true if the path points to a C# source file (not a .csproj/.sln/directory).
    /// </summary>
    public static bool IsSourceFile(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
        !path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Extracts top-level class/struct/record names from a .cs file using simple text scanning.
    /// Returns empty list if the file cannot be read or has no type declarations.
    /// </summary>
    public static List<string> ExtractTypeNames(string csFilePath)
    {
        var results = new List<string>();
        if (!File.Exists(csFilePath)) return results;

        try
        {
            var regex = new System.Text.RegularExpressions.Regex(
                @"(?:^|\s)(?:public|internal|private|protected|static|sealed|abstract|partial|\s)*\s*(?:class|struct|record)\s+(\w+)",
                System.Text.RegularExpressions.RegexOptions.Multiline);

            var content = File.ReadAllText(csFilePath);
            foreach (System.Text.RegularExpressions.Match match in regex.Matches(content))
            {
                var name = match.Groups[1].Value;
                if (!results.Contains(name))
                    results.Add(name);
            }
        }
        catch
        {
            // Don't fail if we can't read the file
        }

        return results;
    }

    /// <summary>
    /// Builds a dotnet test filter expression scoping to the types in a source file.
    /// Combines with an existing filter using &amp;. Returns the original filter if
    /// type names cannot be extracted.
    /// </summary>
    public static string? BuildSourceFileFilter(string csFilePath, string? existingFilter)
    {
        var typeNames = ExtractTypeNames(csFilePath);
        if (typeNames.Count == 0) return existingFilter;

        var classFilter = typeNames.Count == 1
            ? $"FullyQualifiedName~.{typeNames[0]}."
            : $"({string.Join(" | ", typeNames.Select(t => $"FullyQualifiedName~.{t}."))})";

        if (string.IsNullOrWhiteSpace(existingFilter))
            return classFilter;

        return $"({existingFilter}) & {classFilter}";
    }

    /// <summary>
    /// Builds a VSTest /TestCaseFilter expression scoping to the types in a source file.
    /// </summary>
    public static string? BuildSourceFileVsTestFilter(string csFilePath, string? existingFilter)
    {
        var typeNames = ExtractTypeNames(csFilePath);
        if (typeNames.Count == 0) return existingFilter;

        var classFilter = typeNames.Count == 1
            ? $"FullyQualifiedName~.{typeNames[0]}."
            : $"({string.Join(" | ", typeNames.Select(t => $"FullyQualifiedName~.{t}."))})";

        if (string.IsNullOrWhiteSpace(existingFilter))
            return classFilter;

        return $"({existingFilter}) & {classFilter}";
    }

    /// <summary>
    /// Parses a severity filter string ("error", "warning", "info", "hidden", "all")
    /// into a DiagnosticSeverity. Returns true if valid; result is null for "all".
    /// </summary>
    public static bool TryParseSeverityFilter(string filter, out DiagnosticSeverity? result)
    {
        switch (filter.ToLowerInvariant())
        {
            case "error": result = DiagnosticSeverity.Error; return true;
            case "warning": result = DiagnosticSeverity.Warning; return true;
            case "info": result = DiagnosticSeverity.Info; return true;
            case "hidden": result = DiagnosticSeverity.Hidden; return true;
            case "all": result = null; return true;
            default: result = null; return false;
        }
    }
}

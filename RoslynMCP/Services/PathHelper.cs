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
        if (!slnPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || !File.Exists(slnPath))
            return false;

        var slnDir = Path.GetDirectoryName(slnPath)!;
        try
        {
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
            // Don't fail if we can't read the .sln
        }

        return false;
    }

    /// <summary>
    /// Returns true if the build target (a .csproj or .sln) requires MSBuild
    /// rather than the dotnet CLI.
    /// </summary>
    public static bool RequiresMsBuild(string buildTarget)
    {
        if (buildTarget.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
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

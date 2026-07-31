using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using RoslynMCP.Services;
using RoslynMCP.Services.Testing;

namespace RoslynMCP.Tools;

[McpServerToolType]
public static class DiscoverTestsTool
{
    /// <summary>
    /// Discovers all test methods in a project using Roslyn semantic analysis.
    /// </summary>
    [McpServerTool, Description(
        "Discover all test methods in a .NET test project using static Roslyn analysis. " +
        "Returns test names, frameworks, file paths, and line numbers. " +
        "Useful for understanding test structure without running tests.")]
    public static async Task<string> DiscoverTests(
        [Description("Path to the test project (.csproj) or a source file in the test project.")]
        string projectPath,
        IOutputFormatter fmt,
        [Description("Optional class name filter (partial match). Only returns tests from matching classes.")]
        string? className = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(projectPath))
                return "Error: projectPath cannot be empty.";

            var normalizedInput = PathHelper.NormalizePath(projectPath);
            var csprojPath = PathHelper.ResolveCsprojPath(projectPath);
            if (csprojPath is null)
                return $"Error: Could not find a .csproj file for '{projectPath}'.";

            var sourceFileFilter = PathHelper.IsSourceFile(normalizedInput) ? normalizedInput : null;

            var tests = await TestDiscoveryService.DiscoverAsync(
                csprojPath, className, sourceFileFilter, cancellationToken);

            if (tests.Count == 0)
            {
                return className is not null
                    ? $"No test methods found matching class '{className}' in project."
                    : "No test methods found in project.";
            }

            var sb = new StringBuilder();
            fmt.AppendHeader(sb, $"Test Discovery: {Path.GetFileNameWithoutExtension(csprojPath)}");
            fmt.AppendField(sb, "Tests found", tests.Count);

            foreach (var group in tests.GroupBy(t => t.ClassName).OrderBy(g => g.Key))
            {
                fmt.AppendHeader(sb, $"{group.Key} ({group.First().Framework})", 2);

                fmt.BeginTable(sb, group.Key, ["#", "Method", "File", "Lines"], group.Count());
                int i = 1;
                foreach (var test in group.OrderBy(t => t.StartLine))
                {
                    var projectDir = Path.GetDirectoryName(csprojPath);
                    var relPath = projectDir is not null && test.FilePath is not null
                        ? Path.GetRelativePath(projectDir, test.FilePath)
                        : test.FilePath ?? "";
                    string lineRange = test.EndLine > test.StartLine
                        ? $"{test.StartLine}–{test.EndLine}"
                        : $"{test.StartLine}";
                    fmt.BeginRow(sb);
                    fmt.WriteCell(sb, i++);
                    fmt.WriteCell(sb, test.DisplayName);
                    fmt.WriteCell(sb, relPath);
                    fmt.WriteCell(sb, lineRange);
                    fmt.EndRow(sb);
                }
                fmt.EndTable(sb);
            }

            var frameworks = tests.GroupBy(t => t.Framework).OrderBy(g => g.Key);
            fmt.AppendField(sb, "Frameworks", string.Join(", ", frameworks.Select(g => $"{g.Key} ({g.Count()})")));

            return sb.ToString();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

}

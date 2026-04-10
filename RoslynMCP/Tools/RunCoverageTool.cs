using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

[McpServerToolType]
public static class RunCoverageTool
{
    [McpServerTool, Description(
        "Run code coverage collection on a .NET test project. Set background=true to run in the background " +
        "and continue working — check results later with GetBackgroundTaskResult. " +
        "Must be called before using GetCoverage. Re-run after making code changes that affect coverage.")]
    public static async Task<string> RunCoverage(
        [Description("Path to the test project (.csproj) or a source file in the test project.")]
        string projectPath,
        BackgroundTaskStore taskStore,
        [Description("Optional test filter expression (e.g. 'ClassName.MethodName', 'FullyQualifiedName~MyTest'). If empty, all tests are run.")]
        string? filter = null,
        [Description("Set to true to run coverage in the background. Returns a task ID immediately " +
                     "so you can continue working. Use GetBackgroundTaskResult to check results later.")]
        bool background = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(projectPath))
                return "Error: projectPath cannot be empty.";

            string systemPath = PathHelper.NormalizePath(projectPath);

            if (background)
            {
                var csprojPath = PathHelper.ResolveCsprojPath(systemPath);
                if (csprojPath is null)
                    return $"Error: Could not find a .csproj file for '{projectPath}'.";
                return BackgroundTaskHelper.StartCoverageBackground(
                    csprojPath, filter, taskStore);
            }

            var result = await CoverageService.RunCoverageAsync(systemPath, filter, cancellationToken);
            return result.Message;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}

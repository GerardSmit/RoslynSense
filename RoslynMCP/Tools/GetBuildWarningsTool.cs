using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

[McpServerToolType]
public static class GetBuildWarningsTool
{
    [McpServerTool, Description(
        "Get all build warnings of a specific warning code (e.g. CS0414) from the most recent " +
        "BuildProject run for the given project. Run BuildProject first to populate the warning cache.")]
    public static string GetBuildWarnings(
        [Description("Path to the .csproj, .sln file, or a source file in the project. " +
                     "Must match the path used in the preceding BuildProject call.")]
        string projectPath,
        [Description("Warning code to retrieve, e.g. CS0414 or CS1066.")]
        string warningCode,
        BuildWarningsStore warningsStore)
    {
        var resolved = BuildProjectTool.ResolveBuildTarget(projectPath);
        if (resolved.StartsWith("Error:", StringComparison.Ordinal))
            return resolved;

        var warnings = warningsStore.GetWarnings(resolved, warningCode);

        if (warnings is null)
            return $"No cached build data found for '{Path.GetFileName(resolved)}'. " +
                   $"Run BuildProject first, then call GetBuildWarnings.";

        var code = warningCode.ToUpperInvariant();

        if (warnings.Count == 0)
            return $"No warnings with code {code} in the last build of '{Path.GetFileName(resolved)}'.";

        var sb = new StringBuilder();
        sb.AppendLine($"**{code} warnings in {Path.GetFileName(resolved)} ({warnings.Count} total):**");
        sb.AppendLine("```");
        foreach (var line in warnings)
            sb.AppendLine(line);
        sb.AppendLine("```");

        return sb.ToString();
    }
}

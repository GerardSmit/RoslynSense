namespace RoslynMCP.Services;

/// <summary>Global properties shared by the seed workspace, evaluation prewarm and pooled hosts.</summary>
internal static class WorkspaceBuildProperties
{
    public static Dictionary<string, string> Create(bool isLegacy, string? solutionPath)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DesignTimeBuild"] = "true",
        };
        if (!isLegacy)
            properties["AlwaysUseNETSdkDefaults"] = "true";

        if (!string.IsNullOrEmpty(solutionPath))
        {
            string fullPath = Path.GetFullPath(solutionPath);
            string directory = Path.GetDirectoryName(fullPath)!;
            properties["SolutionDir"] = Path.EndsInDirectorySeparator(directory)
                ? directory : directory + Path.DirectorySeparatorChar;
            properties["SolutionPath"] = fullPath;
            properties["SolutionName"] = Path.GetFileNameWithoutExtension(fullPath);
            properties["SolutionFileName"] = Path.GetFileName(fullPath);
            properties["SolutionExt"] = Path.GetExtension(fullPath);
        }

        // An SDK project can reference a legacy web project. MSBuild evaluates that reference
        // inside the SDK host when requesting its target frameworks, before Roslyn can route it
        // to a legacy host. Supply the actual installed web targets for those nested evaluations.
        // VisualStudioVersion is deliberately left to the selected toolset.
        if (OperatingSystem.IsWindows()
            && MsBuildLocator.VsEvaluationProperties.TryGetValue("VSToolsPath", out string? targets))
            properties["VSToolsPath"] = targets;

        return properties;
    }
}

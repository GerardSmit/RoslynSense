using Xunit;

namespace RoslynMCP.Tests;

public class WorkflowConfigurationTests
{
    [Fact]
    public void CiWorkflowExistsAndUsesDotnet10()
    {
        string content = File.ReadAllText(GetRepoPath(".github", "workflows", "ci.yml"));

        Assert.Contains("dotnet-version: 10.0.x", content, StringComparison.Ordinal);
        Assert.Contains("dotnet build RoslynMCP.sln --configuration Release --no-restore --nologo", content, StringComparison.Ordinal);
        Assert.Contains("dotnet test RoslynMCP.sln", content, StringComparison.Ordinal);
        Assert.Contains("--configuration Release", content, StringComparison.Ordinal);
        Assert.Contains("--no-build", content, StringComparison.Ordinal);
        Assert.Contains("-p:UseAppHost=false", content, StringComparison.Ordinal);
        Assert.Contains("--blame-hang", content, StringComparison.Ordinal);
        Assert.Contains("--blame-hang-dump-type mini", content, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseWorkflowExistsWithTagDrivenVersioning()
    {
        string content = File.ReadAllText(GetRepoPath(".github", "workflows", "ci.yml"));

        // Release job gate
        Assert.Contains("refs/tags/v", content, StringComparison.Ordinal);
        // Workflow dispatch with bump input
        Assert.Contains("workflow_dispatch:", content, StringComparison.Ordinal);
        Assert.Contains("bump:", content, StringComparison.Ordinal);
        // Version computation
        Assert.Contains("Determine version", content, StringComparison.Ordinal);
        Assert.Contains("fetch-depth: 0", content, StringComparison.Ordinal);
        // Build, pack, publish
        Assert.Contains("dotnet pack RoslynMCP/RoslynMCP.csproj", content, StringComparison.Ordinal);
        Assert.Contains("RoslynSense", content, StringComparison.Ordinal);
        Assert.Contains("dotnet nuget push", content, StringComparison.Ordinal);
        Assert.Contains("NUGET_API_KEY", content, StringComparison.Ordinal);
        Assert.Contains("uses: azure/login@v3", content, StringComparison.Ordinal);

        string packageStep = GetWorkflowStep(content, "Package the extension at the release version");
        Assert.Contains(
            "EXTENSION_PRERELEASE: ${{ steps.version.outputs.extension_prerelease }}",
            packageStep,
            StringComparison.Ordinal);
        Assert.Contains("$vsceArgs += '--pre-release'", packageStep, StringComparison.Ordinal);
        // GitHub Release
        Assert.Contains("softprops/action-gh-release", content, StringComparison.Ordinal);
        Assert.Contains("contents: write", content, StringComparison.Ordinal);
    }

    private static string GetWorkflowStep(string workflow, string stepName)
    {
        string marker = $"- name: {stepName}";
        int start = workflow.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Workflow step '{stepName}' was not found.");

        int next = workflow.IndexOf("\n      - name:", start + marker.Length, StringComparison.Ordinal);
        return next >= 0 ? workflow[start..next] : workflow[start..];
    }

    private static string GetRepoPath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string solutionPath = Path.Combine(directory.FullName, "RoslynMCP.sln");
            if (File.Exists(solutionPath))
                return Path.Combine([directory.FullName, .. parts]);

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}

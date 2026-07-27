using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Covers the unified project classifier that replaced the previous ad-hoc checks
/// (<c>PathHelper.RequiresMsBuild</c>, <c>WorkspaceService.InferTargetFrameworkKind</c>,
/// <c>ProjectStructureTool.InferTargetFramework</c> and the <c>ListProjects</c> text scan).
/// </summary>
public class ProjectClassifierTests
{
    [Fact]
    public void WhenSdkStyleLibraryThenItIsModernAndBuildsWithTheDotnetCli()
    {
        var classification = ProjectClassifier.Classify(FixturePaths.SampleProjectFile);

        Assert.Equal(ProjectStyle.Sdk, classification.Style);
        Assert.Equal(RuntimeFlavor.NetCore, classification.Runtime);
        Assert.Equal(BuildTool.DotnetCli, classification.BuildTool);
        Assert.Equal(DebugRuntime.CoreClr, classification.DebugRuntime);
        Assert.False(classification.IsRunnable);
    }

    [Fact]
    public void WhenLegacyProjectThenItIsNetFrameworkAndNeedsVisualStudioMsBuild()
    {
        var classification = ProjectClassifier.Classify(FixturePaths.LegacyProjectFile);

        Assert.Equal(ProjectStyle.Legacy, classification.Style);
        Assert.Equal(RuntimeFlavor.NetFramework, classification.Runtime);
        Assert.Equal("net472", classification.TargetFramework);
        Assert.Equal(BuildTool.VisualStudioMsBuild, classification.BuildTool);
        Assert.Equal(DebugRuntime.NetFramework, classification.DebugRuntime);
    }

    [Fact]
    public void WhenClassifiedThenRequiresMsBuildAgreesWithTheBuildTool()
    {
        // PathHelper.RequiresMsBuild now delegates here; the two must not diverge.
        foreach (var project in new[]
                 {
                     FixturePaths.SampleProjectFile,
                     FixturePaths.LegacyProjectFile,
                     FixturePaths.AspxProjectFile,
                     FixturePaths.WebFormsSiteFile,
                 })
        {
            var expected = ProjectClassifier.Classify(project).BuildTool == BuildTool.VisualStudioMsBuild;
            Assert.Equal(expected, PathHelper.RequiresMsBuild(project));
        }
    }

    [Fact]
    public void WhenBlazorProjectThenItIsRecognisedAsAWebApp()
    {
        var classification = ProjectClassifier.Classify(FixturePaths.BlazorProjectFile);

        Assert.Equal(AppKind.AspNetCore, classification.Kind);
        Assert.True(classification.IsRunnable);
    }

    [Fact]
    public void WhenTestProjectThenItIsFlaggedAsSuch() =>
        Assert.True(ProjectClassifier.Classify(FixturePaths.DebugTestProjectFile).IsTestProject);

    [Fact]
    public void WhenProjectFileIsMissingThenClassificationIsUnknownRatherThanThrowing()
    {
        var classification = ProjectClassifier.Classify(
            Path.Combine(Path.GetTempPath(), "definitely-not-here.csproj"));

        Assert.Equal(AppKind.Unknown, classification.Kind);
        Assert.False(classification.IsRunnable);
    }

    [Fact]
    public void WhenClassifiedTwiceThenTheCachedResultIsReturned()
    {
        var first = ProjectClassifier.Classify(FixturePaths.SampleProjectFile);
        var second = ProjectClassifier.Classify(FixturePaths.SampleProjectFile);

        Assert.Same(first, second);
    }

    [Fact]
    public async Task WhenRoslynProjectClassifiedThenItAgreesWithTheFileOnlyResult()
    {
        var fromFile = ProjectClassifier.Classify(FixturePaths.SampleProjectFile);
        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(FixturePaths.SampleProjectFile);
        var fromRoslyn = ProjectClassifier.Classify(project);

        // The Roslyn path refines the file-only result; it must never contradict it.
        Assert.Equal(fromFile.Style, fromRoslyn.Style);
        Assert.Equal(fromFile.Runtime, fromRoslyn.Runtime);
        Assert.Equal(fromFile.BuildTool, fromRoslyn.BuildTool);
        Assert.Equal(fromFile.DebugRuntime, fromRoslyn.DebugRuntime);
        Assert.Equal(fromFile.Kind, fromRoslyn.Kind);
    }
}

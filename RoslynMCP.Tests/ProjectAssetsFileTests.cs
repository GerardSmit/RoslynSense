using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Where the restore output of a project is looked for.
/// </summary>
/// <remarks>
/// A legacy web project that relocates its intermediates with
/// <c>&lt;BaseIntermediateOutputPath&gt;obj\$(MSBuildProjectName)\&lt;/BaseIntermediateOutputPath&gt;</c>
/// was restored before every load, its evaluation cache never noticed a restore, and its restore
/// watcher watched a directory nothing wrote to — all three assumed <c>obj\project.assets.json</c>.
/// </remarks>
public sealed class ProjectAssetsFileTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "roslyn-sense-tests", "assets-file", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void DefaultsToObjBesideTheProject()
    {
        string project = WriteProject("Plain", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        Assert.Equal(
            Path.Combine(Path.GetDirectoryName(project)!, "obj", "project.assets.json"),
            ProjectAssetsFile.Resolve(project));
    }

    [Fact]
    public void FollowsARelocatedBaseIntermediateOutputPath()
    {
        string project = WriteProject("Web.Site", Legacy(
            "<BaseIntermediateOutputPath>obj\\$(MSBuildProjectName)\\</BaseIntermediateOutputPath>"));

        Assert.Equal(
            Path.Combine(Path.GetDirectoryName(project)!, "obj", "Web.Site", "project.assets.json"),
            ProjectAssetsFile.Resolve(project));
    }

    [Fact]
    public void PrefersMSBuildProjectExtensionsPathOverTheBasePath()
    {
        // NuGet writes to the extensions path; the base path is only its default.
        string project = WriteProject("Both", Legacy(
            "<BaseIntermediateOutputPath>obj\\base\\</BaseIntermediateOutputPath>" +
            "<MSBuildProjectExtensionsPath>$(MSBuildProjectDirectory)\\..\\ext\\</MSBuildProjectExtensionsPath>"));

        Assert.Equal(
            Path.GetFullPath(Path.Combine(_root, "ext", "project.assets.json")),
            ProjectAssetsFile.Resolve(project));
    }

    [Fact]
    public void ReadsTheNearestDirectoryBuildProps()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"),
            "<Project><PropertyGroup>" +
            "<BaseIntermediateOutputPath>$(MSBuildThisFileDirectory)obj\\$(MSBuildProjectName)\\</BaseIntermediateOutputPath>" +
            "</PropertyGroup></Project>");
        string project = WriteProject("Lib", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        Assert.Equal(
            Path.Combine(_root, "obj", "Lib", "project.assets.json"),
            ProjectAssetsFile.Resolve(project));
    }

    [Fact]
    public void FollowsTheArtifactsLayout()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"),
            "<Project><PropertyGroup><UseArtifactsOutput>true</UseArtifactsOutput></PropertyGroup></Project>");
        string project = WriteProject("Lib", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        Assert.Equal(
            Path.Combine(_root, "artifacts", "obj", "Lib", "project.assets.json"),
            ProjectAssetsFile.Resolve(project));
    }

    [Fact]
    public void AnExistingDefaultWinsWithoutReadingAnything()
    {
        string project = WriteProject("Restored", Legacy(
            "<BaseIntermediateOutputPath>obj\\$(MSBuildProjectName)\\</BaseIntermediateOutputPath>"));
        string standard = Path.Combine(Path.GetDirectoryName(project)!, "obj", "project.assets.json");
        Directory.CreateDirectory(Path.GetDirectoryName(standard)!);
        File.WriteAllText(standard, "{}");

        // The file on disk is the truth about where the last restore wrote.
        Assert.Equal(standard, ProjectAssetsFile.Resolve(project));
    }

    [Fact]
    public void AValueNeedingAnEvaluationFallsBackToTheDefault()
    {
        string project = WriteProject("Fancy", Legacy(
            "<BaseIntermediateOutputPath>$(SomeRoot)\\obj\\</BaseIntermediateOutputPath>"));

        Assert.Equal(
            Path.Combine(Path.GetDirectoryName(project)!, "obj", "project.assets.json"),
            ProjectAssetsFile.Resolve(project));
    }

    private string WriteProject(string name, string xml)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"{name}.csproj");
        File.WriteAllText(path, xml);
        return path;
    }

    private static string Legacy(string properties) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <PropertyGroup>
            {properties}
            <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>
          </PropertyGroup>
          <Import Project="$(MSBuildBinPath)\Microsoft.CSharp.targets" />
        </Project>
        """;
}

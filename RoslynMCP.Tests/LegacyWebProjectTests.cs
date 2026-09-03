using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Services.ProjectModel;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// A project that imports Visual Studio's own targets, and a tree that has to stay honest when it
/// cannot read one.
/// </summary>
/// <remarks>
/// A legacy ASP.NET project reaches Visual Studio through
/// <c>$(VSToolsPath)\WebApplications\Microsoft.WebApplication.targets</c>. Evaluated with no global
/// properties, <c>VSToolsPath</c> falls back to
/// <c>$(MSBuildExtensionsPath32)\Microsoft\VisualStudio\v$(VisualStudioVersion)</c> — and because
/// MSBuildLocator points <c>MSBuildExtensionsPath32</c> at the .NET SDK so
/// <c>MSBuildWorkspace</c> can find it, that resolves to a path under
/// <c>C:\Program Files\dotnet\sdk\…</c> which has never existed and never could.
/// </remarks>
public class LegacyWebProjectTests
{
    private const string WebProject = """
        <?xml version="1.0" encoding="utf-8"?>
        <Project ToolsVersion="4.0" DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <Import Project="$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props" Condition="Exists('$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props')" />
          <PropertyGroup>
            <Configuration Condition=" '$(Configuration)' == '' ">Debug</Configuration>
            <Platform Condition=" '$(Platform)' == '' ">AnyCPU</Platform>
            <OutputType>Library</OutputType>
            <RootNamespace>Contoso.Web</RootNamespace>
            <AssemblyName>Contoso.Web</AssemblyName>
            <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
          </PropertyGroup>
          <ItemGroup>
            <Compile Include="Default.aspx.cs" />
          </ItemGroup>
          <PropertyGroup>
            <VisualStudioVersion Condition="'$(VisualStudioVersion)' == ''">10.0</VisualStudioVersion>
            <VSToolsPath Condition="'$(VSToolsPath)' == ''">$(MSBuildExtensionsPath32)\Microsoft\VisualStudio\v$(VisualStudioVersion)</VSToolsPath>
          </PropertyGroup>
          <Import Project="$(MSBuildBinPath)\Microsoft.CSharp.targets" />
          <Import Project="$(VSToolsPath)\WebApplications\Microsoft.WebApplication.targets" Condition="'$(VSToolsPath)' != ''" />
        </Project>
        """;

    [Fact]
    public async Task AProjectImportingVisualStudiosWebTargetsEvaluates()
    {
        WorkspaceService.EnsureRegistered();

        if (MsBuildLocator.VsEvaluationProperties.Count == 0)
            return; // No Visual Studio on this machine; there is nothing to import from.

        string dir = Path.Combine(Path.GetTempPath(), $"legacyweb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            string project = Path.Combine(dir, "Contoso.Web.csproj");
            await File.WriteAllTextAsync(project, WebProject);
            await File.WriteAllTextAsync(Path.Combine(dir, "Default.aspx.cs"), "public partial class Default { }");

            var evaluation = await ProjectEvaluationService.EvaluateAsync(project, default);

            Assert.NotNull(evaluation);
            Assert.Contains(
                evaluation!.Items,
                item => string.Equals(Path.GetFileName(item.FullPath), "Default.aspx.cs", StringComparison.OrdinalIgnoreCase));

            // The import resolved to Visual Studio rather than to the .NET SDK, which is the whole
            // point — the SDK path it used to land in cannot contain this file.
            Assert.Contains(
                evaluation.Imports,
                import => import.EndsWith("Microsoft.WebApplication.targets", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// A project the tool cannot evaluate must not have every one of its files labelled as not
    /// belonging to it.
    /// </summary>
    /// <remarks>
    /// The tree reads the item list cached-only so it never blocks on MSBuild, and with no list it
    /// shows every file — which is right. What was wrong was dimming them all and captioning them
    /// "not in project", because that is not a cautious answer to "does this file belong here", it
    /// is a confident wrong one, given about every file at once. It showed while an evaluation was
    /// still running and permanently for a project that could not be evaluated at all, so the first
    /// thing a user saw of an unreadable project was the claim that none of it was real.
    /// </remarks>
    [Fact]
    public async Task AProjectThatCannotBeEvaluatedDoesNotLabelItsFilesNotInProject()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"unevaluatable-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            // Imports a file that does not exist and cannot be conditioned away, so evaluation
            // fails the way an unresolvable $(VSToolsPath) import fails.
            string project = Path.Combine(dir, "Broken.csproj");
            await File.WriteAllTextAsync(project, """
                <Project ToolsVersion="4.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                  <Import Project="$(ThisPathDoesNotResolve)\Nowhere\Missing.targets" />
                </Project>
                """);

            await File.WriteAllTextAsync(Path.Combine(dir, "Program.cs"), "class Program { }");
            await File.WriteAllTextAsync(Path.Combine(dir, "Helper.cs"), "class Helper { }");

            Assert.Null(await ProjectEvaluationService.EvaluateAsync(project, default));

            var nodes = await SolutionTreeHandler.ChildrenAsync(
                new SolutionTreeParams(NodeId: $"project:{project}"), default);

            var files = nodes.Where(n => n.Kind == SolutionNodeKind.File).ToList();
            Assert.NotEmpty(files);

            Assert.All(files, file =>
            {
                Assert.Null(file.Description);
                Assert.False(file.Dimmed);
            });
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}

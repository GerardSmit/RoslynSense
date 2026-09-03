using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// A project loaded twice through the pooled BuildHost must see the second state of the disk, not
/// the first.
/// </summary>
/// <remarks>
/// <para>
/// This is the hazard that comes with keeping a BuildHost alive, and it is silent. Roslyn's
/// <c>BuildHost</c> constructs its <c>ProjectBuildManager</c> once and leaves a batch build open for
/// the life of the process, so <c>LoadProjectAsync</c> goes through
/// <c>projectCollection.GetLoadedProjects(path)</c> and returns the <em>first</em> evaluation of a
/// project for as long as that host lives. Roslyn never trips over it because it disposes the whole
/// host per top-level load; a pool has to handle it itself.
/// </para>
/// <para>
/// Untested, it surfaces as a file added on disk being invisible, an edited <c>.csproj</c> having no
/// effect, and completion returning nothing — with no error anywhere, which is exactly how it was
/// found: seven unrelated-looking tests failing together.
/// </para>
/// </remarks>
public class SharedBuildHostStalenessTests
{
    [RoslynSenseBenchFact]
    public async Task AProjectReloadedThroughThePoolSeesFilesAddedSinceTheFirstLoad()
    {
        WorkspaceService.EnsureRegistered();

        string dir = Path.Combine(Path.GetTempPath(), $"pool-staleness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            string projectPath = Path.Combine(dir, "Probe.csproj");
            await File.WriteAllTextAsync(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(dir, "First.cs"), "namespace P; public class First { }");

            var properties = ImmutableDictionary<string, string>.Empty
                .Add("DesignTimeBuild", "true")
                .Add("AlwaysUseNETSdkDefaults", "true");

            using var workspace = WorkspaceService.CreateWorkspace(TextWriter.Null);

            var before = await SharedBuildHost.LoadAsync(
                workspace, properties, [projectPath],
                () => ProjectMap.Create(workspace.CurrentSolution), CancellationToken.None);

            Assert.Contains(before[0].Documents, d => d.Name == "First.cs");
            Assert.DoesNotContain(before[0].Documents, d => d.Name == "Second.cs");

            // The whole point: a file that appears between the two loads.
            await File.WriteAllTextAsync(Path.Combine(dir, "Second.cs"), "namespace P; public class Second { }");

            var after = await SharedBuildHost.LoadAsync(
                workspace, properties, [projectPath],
                () => ProjectMap.Create(workspace.CurrentSolution), CancellationToken.None);

            Assert.Contains(after[0].Documents, d => d.Name == "Second.cs");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}

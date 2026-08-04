using RoslynMCP.Services.Packages;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The resolved dependency graph, read from project.assets.json.
/// </summary>
/// <remarks>
/// Two defects these pin down. The previous reader stopped after the first target framework, so a
/// multi-targeted project reported one arbitrary framework's graph as the whole answer. And it
/// reported a dependency's requested *range* as its version, where the resolved version lives in
/// that package's own entry — so a package bumped to 13.0.3 by another consumer displayed as
/// whatever range asked for it.
/// </remarks>
public class ProjectAssetsServiceTests : IDisposable
{
    private readonly string _directory;

    public ProjectAssetsServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"assets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_directory, "obj"));
        ProjectAssetsService.Invalidate();
    }

    [Fact]
    public void ResolvesTheTrueVersionNotTheRequestedRange()
    {
        string project = WriteAssets("""
            {
              "targets": {
                "net10.0": {
                  "Consumer/1.0.0": {
                    "type": "package",
                    "dependencies": { "Newtonsoft.Json": "13.0.1" }
                  },
                  "Newtonsoft.Json/13.0.3": { "type": "package" }
                }
              },
              "project": {
                "frameworks": { "net10.0": { "dependencies": { "Consumer": {} } } }
              }
            }
            """);

        var dependencies = ProjectAssetsService.DependenciesOf(project, "Consumer", "net10.0");

        var json = Assert.Single(dependencies);
        Assert.Equal("Newtonsoft.Json", json.Id);
        Assert.Equal("13.0.3", json.Version);
    }

    [Fact]
    public void ReadsEveryTargetFrameworkNotJustTheFirst()
    {
        string project = WriteAssets("""
            {
              "targets": {
                "net10.0": { "OnlyModern/2.0.0": { "type": "package" } },
                "netstandard2.0": { "OnlyLegacy/1.0.0": { "type": "package" } }
              },
              "project": { "frameworks": { "net10.0": {}, "netstandard2.0": {} } }
            }
            """);

        var graph = ProjectAssetsService.Read(project);

        Assert.Equal(["net10.0", "netstandard2.0"], graph.TargetFrameworks);
        Assert.Contains(graph.Packages, p => p.Id == "OnlyModern" && p.TargetFramework == "net10.0");
        Assert.Contains(graph.Packages, p => p.Id == "OnlyLegacy" && p.TargetFramework == "netstandard2.0");
    }

    [Fact]
    public void TransitiveExcludesWhatTheProjectReferencesDirectly()
    {
        string project = WriteAssets("""
            {
              "targets": {
                "net10.0": {
                  "Direct/1.0.0": { "type": "package", "dependencies": { "Indirect": "2.0.0" } },
                  "Indirect/2.0.0": { "type": "package" },
                  "SomeProject/1.0.0": { "type": "project" }
                }
              },
              "project": { "frameworks": { "net10.0": { "dependencies": { "Direct": {} } } } }
            }
            """);

        var transitive = ProjectAssetsService.TransitiveOnly(project, "net10.0");

        var indirect = Assert.Single(transitive);
        Assert.Equal("Indirect", indirect.Id);
        // Project references live in targets too, but they are not packages.
        Assert.DoesNotContain(transitive, p => p.Id == "SomeProject");
    }

    [Fact]
    public void APackageResolvedForSeveralRuntimesDoesNotCrashTheLookup()
    {
        // A project with a RuntimeIdentifier gets one target per framework/RID pair, and both fold
        // to the same moniker — so the same package appears twice with the same TargetFramework.
        // Building the lookup without deduplicating threw "an item with the same key has already
        // been added" the moment anyone expanded a package node in the Solution Explorer.
        string project = WriteAssets("""
            {
              "targets": {
                "net10.0": {
                  "Consumer/1.0.0": { "type": "package", "dependencies": { "Azure.Core": "1.44.1" } },
                  "Azure.Core/1.44.1": { "type": "package" }
                },
                "net10.0/win-x64": {
                  "Consumer/1.0.0": { "type": "package", "dependencies": { "Azure.Core": "1.44.1" } },
                  "Azure.Core/1.44.1": { "type": "package" }
                }
              },
              "project": { "frameworks": { "net10.0": { "dependencies": { "Consumer": {} } } } }
            }
            """);

        var dependencies = ProjectAssetsService.DependenciesOf(project, "Consumer", targetFramework: null);

        var core = Assert.Single(dependencies);
        Assert.Equal("Azure.Core", core.Id);

        // The same shape reaches the tree through the transitive listing.
        Assert.Single(ProjectAssetsService.TransitiveOnly(project, targetFramework: null));
    }

    [Fact]
    public void DependenciesOfAPackageThatIsNotInTheGraphIsEmpty() =>
        Assert.Empty(ProjectAssetsService.DependenciesOf(
            WriteAssets("""{ "targets": { "net10.0": {} }, "project": { "frameworks": {} } }"""),
            "Nonexistent",
            targetFramework: null));

    [Fact]
    public void RuntimeSpecificTargetsFoldIntoTheirFramework()
    {
        string project = WriteAssets("""
            {
              "targets": {
                "net10.0/win-x64": { "Native/1.0.0": { "type": "package" } }
              },
              "project": { "frameworks": { "net10.0": {} } }
            }
            """);

        var graph = ProjectAssetsService.Read(project);

        Assert.Equal(["net10.0"], graph.TargetFrameworks);
    }

    [Fact]
    public void MissingAssetsFileIsEmpty()
    {
        var graph = ProjectAssetsService.Read(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}", "App.csproj"));

        Assert.Empty(graph.Packages);
        Assert.Empty(graph.TargetFrameworks);
    }

    [Fact]
    public void MalformedAssetsFileIsEmptyRatherThanThrowing()
    {
        string project = WriteAssets("{ not json");

        Assert.Empty(ProjectAssetsService.Read(project).Packages);
    }

    private string WriteAssets(string json)
    {
        File.WriteAllText(Path.Combine(_directory, "obj", "project.assets.json"), json);
        string project = Path.Combine(_directory, "App.csproj");
        File.WriteAllText(project, "<Project />");
        return project;
    }

    public void Dispose()
    {
        ProjectAssetsService.Invalidate();
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}

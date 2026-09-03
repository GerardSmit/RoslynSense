using Microsoft.CodeAnalysis;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The healer re-binds references that evaluation dropped over unbuilt outputs: a
/// <c>&lt;ProjectReference&gt;</c> to a loaded project, and a <c>&lt;Reference&gt;</c> whose
/// <c>HintPath</c> is a loaded project's unbuilt output — and nothing else.
/// </summary>
public class ProjectReferenceHealerTests : IDisposable
{
    private readonly string _root;
    private readonly AdhocWorkspace _workspace = new();

    public ProjectReferenceHealerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ref-heal-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "App"));
        Directory.CreateDirectory(Path.Combine(_root, "Lib"));
    }

    public void Dispose()
    {
        _workspace.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private ProjectId AddProject(string name, string? projectFileContent, string? outputPath = null)
    {
        string file = Path.Combine(_root, name, name + ".csproj");
        if (projectFileContent is not null)
            File.WriteAllText(file, projectFileContent);

        var info = ProjectInfo.Create(
            ProjectId.CreateNewId(name), VersionStamp.Create(), name, name,
            LanguageNames.CSharp, filePath: file,
            outputFilePath: outputPath is null ? null : Path.Combine(_root, name, outputPath));

        _workspace.AddProject(info);
        return info.Id;
    }

    private IEnumerable<ProjectId> ReferencesOf(ProjectId id) =>
        _workspace.CurrentSolution.GetProject(id)!.ProjectReferences.Select(r => r.ProjectId);

    [Fact]
    public void RebindsADroppedProjectReference()
    {
        var app = AddProject("App", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="..\Lib\Lib.csproj" />
              </ItemGroup>
            </Project>
            """);
        var lib = AddProject("Lib", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        ProjectReferenceHealer.Heal(_workspace);

        Assert.Equal([lib], ReferencesOf(app));
    }

    [Fact]
    public void RebindsAHintPathToALoadedProjectsUnbuiltOutput()
    {
        var app = AddProject("App", """
            <Project ToolsVersion="4.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup>
                <Reference Include="Lib">
                  <HintPath>..\Lib\bin\Lib.dll</HintPath>
                </Reference>
              </ItemGroup>
            </Project>
            """);
        var lib = AddProject("Lib", "<Project Sdk=\"Microsoft.NET.Sdk\" />", outputPath: @"bin\Lib.dll");

        ProjectReferenceHealer.Heal(_workspace);

        Assert.Equal([lib], ReferencesOf(app));
    }

    [Fact]
    public void LeavesAHintPathAloneWhenTheAssemblyExists()
    {
        var app = AddProject("App", """
            <Project ToolsVersion="4.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup>
                <Reference Include="Lib">
                  <HintPath>..\Lib\bin\Lib.dll</HintPath>
                </Reference>
              </ItemGroup>
            </Project>
            """);
        AddProject("Lib", "<Project Sdk=\"Microsoft.NET.Sdk\" />", outputPath: @"bin\Lib.dll");

        // Built on disk: RAR resolved this the normal way, and the rewiring that runs after
        // every add owns the conversion. The healer stays out of it.
        Directory.CreateDirectory(Path.Combine(_root, "Lib", "bin"));
        File.WriteAllText(Path.Combine(_root, "Lib", "bin", "Lib.dll"), "");

        ProjectReferenceHealer.Heal(_workspace);

        Assert.Empty(ReferencesOf(app));
    }

    [Fact]
    public void AddsNothingTwice()
    {
        var app = AddProject("App", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="..\Lib\Lib.csproj" />
              </ItemGroup>
            </Project>
            """);
        var lib = AddProject("Lib", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        ProjectReferenceHealer.Heal(_workspace);
        ProjectReferenceHealer.Heal(_workspace);

        Assert.Equal([lib], ReferencesOf(app));
    }

    [Fact]
    public void SkipsAnAddThatWouldCreateACycle()
    {
        var app = AddProject("App", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="..\Lib\Lib.csproj" />
              </ItemGroup>
            </Project>
            """);
        var lib = AddProject("Lib", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var withBackRef = _workspace.CurrentSolution
            .AddProjectReference(lib, new ProjectReference(app));
        Assert.True(_workspace.TryApplyChanges(withBackRef));

        ProjectReferenceHealer.Heal(_workspace);

        Assert.Empty(ReferencesOf(app));
    }

    [Fact]
    public void NeverHealsAnAnalyzerOnlyProjectReference()
    {
        var app = AddProject("App", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="..\Lib\Lib.csproj"
                                  ReferenceOutputAssembly="false" OutputItemType="Analyzer" />
              </ItemGroup>
            </Project>
            """);
        AddProject("Lib", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var missing = ProjectReferenceHealer.Heal(_workspace);

        // A source-generator relationship is not a compilation reference: nothing to add,
        // nothing to load.
        Assert.Empty(ReferencesOf(app));
        Assert.Empty(missing);
    }

    [Fact]
    public void ReportsALoadableDroppedTargetForTheCallerToLoad()
    {
        AddProject("App", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="..\Lib\Lib.csproj" />
              </ItemGroup>
            </Project>
            """);
        string libProject = Path.Combine(_root, "Lib", "Lib.csproj");
        File.WriteAllText(libProject, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var missing = ProjectReferenceHealer.Heal(_workspace);

        Assert.Equal<string>([libProject], missing);
    }

    [Fact]
    public void FindsTheProjectFileBesideAHintedAssembly()
    {
        AddProject("App", """
            <Project ToolsVersion="4.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup>
                <Reference Include="Lib">
                  <HintPath>..\Lib\bin\Lib.dll</HintPath>
                </Reference>
              </ItemGroup>
            </Project>
            """);
        string libProject = Path.Combine(_root, "Lib", "Lib.csproj");
        File.WriteAllText(libProject, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        // The hinted DLL does not exist and nothing loaded produces it — but the project file
        // sitting above the hinted bin does, so it is offered for loading.
        var missing = ProjectReferenceHealer.Heal(_workspace);

        Assert.Equal<string>([libProject], missing);
    }

    [Fact]
    public void IgnoresIntentsWhoseTargetIsNotLoaded()
    {
        var app = AddProject("App", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="..\Absent\Absent.csproj" />
                <Reference Include="AlsoAbsent">
                  <HintPath>..\Absent\bin\AlsoAbsent.dll</HintPath>
                </Reference>
              </ItemGroup>
            </Project>
            """);

        ProjectReferenceHealer.Heal(_workspace);

        Assert.Empty(ReferencesOf(app));
    }
}

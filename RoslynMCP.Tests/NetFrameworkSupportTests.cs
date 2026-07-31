using RoslynMCP.Services.Packages;
using RoslynMCP.Services.ProjectModel;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The legacy project format, which the dotnet CLI refuses to touch: packages.config package
/// management and non-SDK project references.
/// </summary>
public class NetFrameworkSupportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"netfx-{Guid.NewGuid():N}");

    public NetFrameworkSupportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private const string LegacyProject = """
        <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <PropertyGroup>
            <ProjectGuid>{11111111-2222-3333-4444-555555555555}</ProjectGuid>
            <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>
          </PropertyGroup>
          <ItemGroup>
            <Reference Include="System" />
          </ItemGroup>
        </Project>
        """;

    private const string SdkProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    private string WriteProject(string name, string contents, bool packagesConfig = false)
    {
        string directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{name}.csproj");
        File.WriteAllText(path, contents);

        if (packagesConfig)
        {
            File.WriteAllText(Path.Combine(directory, "packages.config"),
                """<?xml version="1.0" encoding="utf-8"?><packages></packages>""");
        }
        return path;
    }

    // === Project format detection ===

    [Fact]
    public void TheProjectFormatDecidesWhichToolCanEditIt()
    {
        Assert.False(ProjectMutationService.IsSdkStyle(WriteProject("Legacy", LegacyProject)));
        Assert.True(ProjectMutationService.IsSdkStyle(WriteProject("Modern", SdkProject)));
    }

    // === Legacy project references ===

    [Fact]
    public async Task ALegacyReferenceIsWrittenWithTheGuidAndNameMsBuildExpects()
    {
        string app = WriteProject("App", LegacyProject);
        string library = WriteProject("Lib", LegacyProject);

        var result = await ProjectMutationService.AddProjectReferenceAsync(app, library);

        Assert.True(result.Ok, result.Message);
        string xml = await File.ReadAllTextAsync(app);
        Assert.Contains("ProjectReference", xml);
        Assert.Contains(@"..\Lib\Lib.csproj", xml);
        // Visual Studio writes both, and tooling that reads the solution expects them.
        Assert.Contains("{11111111-2222-3333-4444-555555555555}", xml);
        Assert.Contains("<Name", xml);
    }

    [Fact]
    public async Task AddingTheSameLegacyReferenceTwiceDoesNotDuplicateIt()
    {
        string app = WriteProject("App", LegacyProject);
        string library = WriteProject("Lib", LegacyProject);

        await ProjectMutationService.AddProjectReferenceAsync(app, library);
        await ProjectMutationService.AddProjectReferenceAsync(app, library);

        string xml = await File.ReadAllTextAsync(app);
        int occurrences = xml.Split("<ProjectReference").Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public async Task RemovingALegacyReferenceTakesTheElementOut()
    {
        string app = WriteProject("App", LegacyProject);
        string library = WriteProject("Lib", LegacyProject);
        await ProjectMutationService.AddProjectReferenceAsync(app, library);

        var result = await ProjectMutationService.RemoveProjectReferenceAsync(app, library);

        Assert.True(result.Ok, result.Message);
        Assert.DoesNotContain("ProjectReference", await File.ReadAllTextAsync(app));
    }

    // === packages.config ===

    [Fact]
    public void APackagesConfigProjectIsRecognisedByItsFile()
    {
        Assert.True(PackagesConfigService.Uses(WriteProject("Legacy", LegacyProject, packagesConfig: true)));
        Assert.False(PackagesConfigService.Uses(WriteProject("Modern", SdkProject)));
    }

    [Fact]
    public void InstalledPackagesAreReadFromPackagesConfig()
    {
        string project = WriteProject("Legacy", LegacyProject, packagesConfig: true);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(project)!, "packages.config"),
            """
            <?xml version="1.0" encoding="utf-8"?>
            <packages>
              <package id="Newtonsoft.Json" version="13.0.3" targetFramework="net472" />
              <package id="log4net" version="2.0.15" targetFramework="net472" />
            </packages>
            """);

        var packages = PackagesConfigService.Read(project);

        Assert.Equal(2, packages.Count);
        Assert.Equal("log4net", packages[0].Id);
        Assert.Equal("13.0.3", packages.Single(p => p.Id == "Newtonsoft.Json").Version);
    }

    [Fact]
    public async Task UninstallingSomethingThatIsNotInstalledSaysSo()
    {
        string project = WriteProject("Legacy", LegacyProject, packagesConfig: true);

        var result = await PackagesConfigService.UninstallAsync(project, "Newtonsoft.Json", default);

        Assert.False(result.Success);
        Assert.Contains("not installed", result.Message);
    }

    [Fact]
    public async Task AModernProjectIsRefusedByTheLegacyInstaller()
    {
        string project = WriteProject("Modern", SdkProject);

        var result = await PackagesConfigService.InstallAsync(project, "Newtonsoft.Json", "13.0.3", default);

        Assert.False(result.Success);
        Assert.Contains("packages.config", result.Message);
    }

    [Fact]
    public void ThePackagesFolderIsSolutionWideAsNuGetPutsIt()
    {
        string solution = Path.Combine(_root, "App.sln");
        File.WriteAllText(solution, "");
        string project = WriteProject("Legacy", LegacyProject, packagesConfig: true);

        // Every packages.config project in a solution shares one packages folder beside the .sln.
        Assert.Equal(
            Path.Combine(_root, "packages"),
            PackagesConfigService.PackagesRootFor(project));
    }

    // === lib folder selection ===

    [Theory]
    [InlineData("net472", "net472", true)]
    [InlineData("net45", "net472", true)]
    [InlineData("net48", "net472", false)]
    [InlineData("netstandard2.0", "net472", true)]
    [InlineData("netstandard2.1", "net472", false)]
    [InlineData("netcoreapp3.1", "net472", false)]
    public void OnlyCompatibleLibFoldersAreConsidered(string folder, string target, bool compatible) =>
        Assert.Equal(compatible, PackagesConfigService.FrameworkScore(folder, target) > 0);

    [Fact]
    public void TheClosestCompatibleFrameworkWins()
    {
        // net45 and net461 are both usable from net472; the newer one is the better match, and
        // netstandard is the fallback below either.
        int net45 = PackagesConfigService.FrameworkScore("net45", "net472");
        int net461 = PackagesConfigService.FrameworkScore("net461", "net472");
        int standard = PackagesConfigService.FrameworkScore("netstandard2.0", "net472");

        Assert.True(net461 > net45, "a newer compatible framework should rank higher");
        Assert.True(net45 > standard, "a framework-specific assembly should beat netstandard");
    }

    [Fact]
    public void AnExactMatchBeatsEverything()
    {
        Assert.True(
            PackagesConfigService.FrameworkScore("net472", "net472") >
            PackagesConfigService.FrameworkScore("net471", "net472"));
    }

    // === Classic ASP.NET under IIS Express ===

    [Fact]
    public void ALegacyWebProjectIsLaunchableRatherThanRefused()
    {
        // The launch list used to mark every Framework project unrunnable and point at the AI
        // session; a web project has to appear as a real F5 target now.
        var classification = RoslynMCP.Services.ProjectClassifier.Classify(FixturePaths.WebFormsSiteFile);

        Assert.Equal(RoslynMCP.Services.AppKind.AspNetClassic, classification.Kind);
        Assert.True(classification.IsRunnable);
        Assert.Equal(RoslynMCP.Services.BuildTool.VisualStudioMsBuild, classification.BuildTool);
    }

    [Fact]
    public void ALegacyWebProjectBuildsWithMsBuildRatherThanTheCli()
    {
        // `dotnet build` cannot build it at all, so the choice of driver is the difference
        // between F5 working and failing before it starts.
        Assert.False(ProjectMutationService.IsSdkStyle(FixturePaths.WebFormsSiteFile));
    }
}

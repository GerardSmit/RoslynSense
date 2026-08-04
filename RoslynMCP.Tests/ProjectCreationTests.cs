using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services.ProjectModel;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Adding things to a solution from the tree: a reference between projects, and a new project
/// from a <c>dotnet new</c> template.
/// </summary>
[Collection(SharedState.Name)]
public class ProjectCreationTests
{
    [Fact]
    public async Task TemplatesComeFromTheInstalledSdk()
    {
        var templates = await ProjectTemplateService.ListAsync(default);

        // Hard-coding a list would go stale the moment a workload is installed, so the point of
        // reading them is that whatever is on this machine shows up. These two always are.
        Assert.NotEmpty(templates);
        Assert.Contains(templates, t => t.ShortName == "classlib");
        Assert.Contains(templates, t => t.ShortName == "console");

        // Parsed by column, so a name containing two spaces must survive intact.
        Assert.All(templates, t =>
        {
            Assert.NotEmpty(t.Name);
            Assert.DoesNotContain(' ', t.ShortName);
        });
    }

    [Fact]
    public async Task TargetFrameworksOfferModernAndFramework()
    {
        var frameworks = await ProjectTemplateService.TargetFrameworksAsync(default);

        Assert.Contains(frameworks, f => f.StartsWith("net", StringComparison.Ordinal) && f.Contains('.'));
        Assert.Contains("net48", frameworks);
    }

    [Fact]
    public async Task OneProjectCanReferenceAnother()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"projref-{Guid.NewGuid():N}");
        string app = Path.Combine(dir, "App", "App.csproj");
        string lib = Path.Combine(dir, "Lib", "Lib.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(app)!);
        Directory.CreateDirectory(Path.GetDirectoryName(lib)!);

        const string Sdk =
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";
        File.WriteAllText(app, Sdk);
        File.WriteAllText(lib, Sdk);

        try
        {
            var result = await SolutionTreeEditHandler.EditAsync(
                new SolutionTreeEditParams(
                    Action: "addProjectReference",
                    ProjectPath: app,
                    DestinationUri: LspConverters.PathToUri(lib)),
                default);

            Assert.True(result.Ok, result.Message);
            Assert.Contains("Lib.csproj", await File.ReadAllTextAsync(app));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ANewProjectIsCreatedAndAddedToTheSolution()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"newproj-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string solutionPath = Path.Combine(dir, "App.sln");
        File.WriteAllText(solutionPath, """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "Libraries", "Libraries", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Global
            EndGlobal
            """);

        try
        {
            var result = await SolutionTreeEditHandler.EditAsync(
                new SolutionTreeEditParams(
                    Action: "addProject",
                    TargetUri: LspConverters.PathToUri(solutionPath),
                    // Created inside the solution folder it was invoked on.
                    ProjectPath: "{11111111-1111-1111-1111-111111111111}",
                    Name: "Contoso.Widgets",
                    Kind: "classlib",
                    TargetFramework: "net10.0"),
                default);

            Assert.True(result.Ok, result.Message);

            string created = Path.Combine(dir, "Contoso.Widgets", "Contoso.Widgets.csproj");
            Assert.True(File.Exists(created), "the project file was not created");
            Assert.Contains("net10.0", await File.ReadAllTextAsync(created));

            var nodes = SolutionFileService.Read(solutionPath);
            var project = nodes.Single(n => n.Name == "Contoso.Widgets");
            Assert.Equal("{11111111-1111-1111-1111-111111111111}", project.ParentId);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}

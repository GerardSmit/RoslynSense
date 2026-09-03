using RoslynMCP.Services.ProjectModel;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The New menu: which templates a project is offered, and what creating one writes.
/// </summary>
public class ItemTemplateTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"item-templates-{Guid.NewGuid():N}");

    public ItemTemplateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private const string Library = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    private const string WinFormsApp = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0-windows</TargetFramework>
            <UseWindowsForms>true</UseWindowsForms>
          </PropertyGroup>
        </Project>
        """;

    private const string LegacyWebSite = """
        <?xml version="1.0" encoding="utf-8"?>
        <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <PropertyGroup>
            <ProjectTypeGuids>{349c5851-65df-11da-9384-00065b846f21};{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}</ProjectTypeGuids>
            <RootNamespace>Shop.Web</RootNamespace>
            <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
          </PropertyGroup>
          <ItemGroup>
          </ItemGroup>
          <ProjectExtensions>
            <VisualStudio>
              <FlavorProperties GUID="{349c5851-65df-11da-9384-00065b846f21}">
                <WebProjectProperties>
                  <UseIIS>True</UseIIS>
                </WebProjectProperties>
              </FlavorProperties>
            </VisualStudio>
          </ProjectExtensions>
        </Project>
        """;

    private string WriteProject(string name, string contents)
    {
        string directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{name}.csproj");
        File.WriteAllText(path, contents);
        return path;
    }

    /// <summary>
    /// The reason the code items are scaffolded here at all: `dotnet new class` puts a class in
    /// the project's root namespace whatever folder it is in, and this does not.
    /// </summary>
    [Fact]
    public async Task AClassInAFolderGetsTheFolderNamespace()
    {
        string project = WriteProject("Orders", Library);
        string folder = Path.Combine(Path.GetDirectoryName(project)!, "Models");
        Directory.CreateDirectory(folder);

        var result = await ItemTemplates.CreateAsync("class", folder, "Basket");

        Assert.True(result.Ok, result.Message);
        string written = await File.ReadAllTextAsync(Assert.Single(result.Paths));
        Assert.Contains("namespace Orders.Models;", written, StringComparison.Ordinal);
        Assert.Contains("public class Basket", written, StringComparison.Ordinal);
    }

    /// <summary>An interface named Foo is almost never wanted; IFoo is.</summary>
    [Fact]
    public async Task AnInterfaceGetsItsI()
    {
        string project = WriteProject("Orders", Library);

        var result = await ItemTemplates.CreateAsync(
            "interface", Path.GetDirectoryName(project)!, "Basket");

        Assert.True(result.Ok, result.Message);
        Assert.EndsWith("IBasket.cs", Assert.Single(result.Paths), StringComparison.Ordinal);
    }

    /// <summary>
    /// A Form is offered where WinForms is switched on and nowhere else — the whole point of
    /// asking the server what applies rather than showing one list everywhere.
    /// </summary>
    [Fact]
    public async Task WhatIsOfferedFollowsWhatTheProjectIs()
    {
        string library = WriteProject("Orders", Library);
        string app = WriteProject("Desk", WinFormsApp);

        var forLibrary = await ItemTemplates.ForAsync(library);
        var forApp = await ItemTemplates.ForAsync(app);

        Assert.DoesNotContain(forLibrary, template => template.Id == "winForm");
        Assert.Contains(forApp, template => template.Id == "winForm");

        // And what every C# project has stays on both.
        Assert.Contains(forLibrary, template => template.Id == "class");
        Assert.Contains(forApp, template => template.Id == "class");
    }

    /// <summary>
    /// A Form is two files, and the designer half has to be nested under the other or the
    /// designer will not open it.
    /// </summary>
    [Fact]
    public async Task AFormIsBothItsHalves()
    {
        string project = WriteProject("Desk", WinFormsApp);

        var result = await ItemTemplates.CreateAsync(
            "winForm", Path.GetDirectoryName(project)!, "MainForm");

        Assert.True(result.Ok, result.Message);
        Assert.Equal(2, result.Paths.Count);
        Assert.Contains(result.Paths, path => path.EndsWith("MainForm.cs", StringComparison.Ordinal));
        Assert.Contains(
            result.Paths, path => path.EndsWith("MainForm.Designer.cs", StringComparison.Ordinal));

        string written = await File.ReadAllTextAsync(project);
        Assert.Contains("<DependentUpon>MainForm.cs</DependentUpon>", written, StringComparison.Ordinal);
        // Globbed already, so the item that carries the metadata is an Update, not a second entry.
        Assert.Contains("Update=\"MainForm.Designer.cs\"", written, StringComparison.Ordinal);
        Assert.DoesNotContain("Include=\"MainForm.Designer.cs\"", written, StringComparison.Ordinal);
    }

    /// <summary>
    /// A legacy System.Web site gets the Web Forms items, and lists every file it is given —
    /// nothing globs there, so a file the project does not name is a file that does not build.
    /// </summary>
    [Fact]
    public async Task ALegacyWebSiteGetsWebFormsAndListsWhatItIsGiven()
    {
        string project = WriteProject("Shop.Web", LegacyWebSite);
        // The classifier asks for the web.config that every System.Web site has beside it.
        await File.WriteAllTextAsync(
            Path.Combine(Path.GetDirectoryName(project)!, "web.config"), "<configuration />");

        var templates = await ItemTemplates.ForAsync(project);
        Assert.Contains(templates, template => template.Id == "webForm");

        var result = await ItemTemplates.CreateAsync(
            "webForm", Path.GetDirectoryName(project)!, "Default");

        Assert.True(result.Ok, result.Message);
        Assert.Equal(3, result.Paths.Count);

        string written = await File.ReadAllTextAsync(project);
        Assert.Contains("<Content Include=\"Default.aspx\"", written, StringComparison.Ordinal);
        Assert.Contains("<Compile Include=\"Default.aspx.cs\"", written, StringComparison.Ordinal);
        Assert.Contains("<SubType>ASPXCodeBehind</SubType>", written, StringComparison.Ordinal);
        Assert.Contains("<DependentUpon>Default.aspx</DependentUpon>", written, StringComparison.Ordinal);
    }

    /// <summary>Nothing is half-created: a name already taken is refused before anything lands.</summary>
    [Fact]
    public async Task ATemplateThatWouldOverwriteIsRefused()
    {
        string project = WriteProject("Desk", WinFormsApp);
        string directory = Path.GetDirectoryName(project)!;
        await File.WriteAllTextAsync(Path.Combine(directory, "MainForm.Designer.cs"), "// mine");

        var result = await ItemTemplates.CreateAsync("winForm", directory, "MainForm");

        Assert.False(result.Ok);
        Assert.False(File.Exists(Path.Combine(directory, "MainForm.cs")));
        Assert.Equal("// mine", await File.ReadAllTextAsync(
            Path.Combine(directory, "MainForm.Designer.cs")));
    }

    /// <summary>
    /// A solution is offered the files that belong beside it, and only the ones it has not
    /// already got — a second Directory.Build.props in one folder is nobody's intention.
    /// </summary>
    [Fact]
    public async Task ASolutionIsOfferedWhatItDoesNotAlreadyHave()
    {
        string solution = Path.Combine(_root, "Shop.sln");
        await File.WriteAllTextAsync(solution, "");
        await File.WriteAllTextAsync(Path.Combine(_root, ".editorconfig"), "root = true");

        var templates = await ItemTemplates.ForAsync(solution);

        Assert.Contains(templates, template => template.Id == "gitignore");
        Assert.DoesNotContain(templates, template => template.Id == "editorconfig");
        // The project-level items are not solution items.
        Assert.DoesNotContain(templates, template => template.Id == "class");
    }
}

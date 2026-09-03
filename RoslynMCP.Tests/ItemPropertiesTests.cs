using RoslynMCP.Languages.DotSettings.Core;
using RoslynMCP.Services.ProjectModel;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// What the Properties panel writes: a file's build action and metadata, and whether a folder
/// contributes its name to namespaces.
/// </summary>
public class ItemPropertiesTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"item-props-{Guid.NewGuid():N}");

    public ItemPropertiesTests()
    {
        Directory.CreateDirectory(_root);
        ReSharperSettings.Clear();
    }

    public void Dispose()
    {
        ReSharperSettings.Clear();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private const string SdkProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    private string WriteProject(string name, string contents = SdkProject)
    {
        string directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{name}.csproj");
        File.WriteAllText(path, contents);
        return path;
    }

    private static string WriteFile(string projectPath, string relative, string contents = "")
    {
        string full = Path.Combine(Path.GetDirectoryName(projectPath)!, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
        return full;
    }

    /// <summary>
    /// The file stays in its glob: an Update carries the metadata, and no Remove appears.
    /// </summary>
    [Fact]
    public async Task MetadataOnAGlobbedFileIsWrittenAsAnUpdate()
    {
        string project = WriteProject("Orders");
        string file = WriteFile(project, Path.Combine("Assets", "data.json"), "{}");

        var result = await ProjectMutationService.SetItemPropertiesAsync(
            project, file, itemType: null,
            new Dictionary<string, string?> { ["CopyToOutputDirectory"] = "PreserveNewest" });

        Assert.True(result.Ok, result.Message);

        string written = await File.ReadAllTextAsync(project);
        Assert.Contains("Update=", written, StringComparison.Ordinal);
        Assert.Contains("<CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>", written,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Remove=", written, StringComparison.Ordinal);
    }

    /// <summary>
    /// Changing the build action has to take the file out of every glob that could have claimed
    /// it — a file listed twice is a build error, not a preference.
    /// </summary>
    [Fact]
    public async Task ChangingTheBuildActionBreaksTheFileOutOfItsGlob()
    {
        string project = WriteProject("Orders");
        string file = WriteFile(project, "Template.cs", "class Template { }");

        var result = await ProjectMutationService.SetItemPropertiesAsync(
            project, file, itemType: "EmbeddedResource");

        Assert.True(result.Ok, result.Message);

        string written = await File.ReadAllTextAsync(project);
        Assert.Contains("""<Compile Remove="Template.cs" />""", written, StringComparison.Ordinal);
        Assert.Contains("""<EmbeddedResource Include="Template.cs" />""", written,
            StringComparison.Ordinal);
    }

    /// <summary>An empty value is "not set", which in the project file is no element at all.</summary>
    [Fact]
    public async Task ClearingAMetadataValueRemovesIt()
    {
        string project = WriteProject("Orders");
        string file = WriteFile(project, Path.Combine("Assets", "data.json"), "{}");

        await ProjectMutationService.SetItemPropertiesAsync(
            project, file, itemType: null,
            new Dictionary<string, string?> { ["CopyToOutputDirectory"] = "Always" });

        var result = await ProjectMutationService.SetItemPropertiesAsync(
            project, file, itemType: null,
            new Dictionary<string, string?> { ["CopyToOutputDirectory"] = "" });

        Assert.True(result.Ok, result.Message);
        Assert.DoesNotContain("CopyToOutputDirectory", await File.ReadAllTextAsync(project),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A marked folder stops contributing its segment, which is the whole of what the setting
    /// does — and the file it is written to is the one Rider would have written.
    /// </summary>
    [Fact]
    public void MarkingAFolderStopsItContributingANamespace()
    {
        string project = WriteProject("Orders");
        string folder = Path.Combine(Path.GetDirectoryName(project)!, "Generated");
        Directory.CreateDirectory(folder);

        Assert.True(DotSettingsWriter.SetNamespaceProvider(project, folder, isProvider: false));

        Assert.True(File.Exists(project + ".DotSettings"));

        var settings = ReSharperSettings.ForProject(project);
        Assert.False(settings.IsNamespaceProvider("Generated"));
        Assert.Empty(settings.NamespaceSegments("Generated"));
    }

    /// <summary>And putting it back leaves the layer saying nothing about the folder.</summary>
    [Fact]
    public void UnmarkingAFolderTakesTheEntryBackOut()
    {
        string project = WriteProject("Orders");
        string folder = Path.Combine(Path.GetDirectoryName(project)!, "Generated");
        Directory.CreateDirectory(folder);

        DotSettingsWriter.SetNamespaceProvider(project, folder, isProvider: false);
        Assert.True(DotSettingsWriter.SetNamespaceProvider(project, folder, isProvider: true));

        Assert.DoesNotContain("NamespaceFoldersToSkip",
            File.ReadAllText(project + ".DotSettings"), StringComparison.Ordinal);

        var settings = ReSharperSettings.ForProject(project);
        Assert.True(settings.IsNamespaceProvider("Generated"));
        Assert.Equal(["Generated"], settings.NamespaceSegments("Generated"));
    }

    /// <summary>
    /// A folder outside the project is not the project's to describe.
    /// </summary>
    [Fact]
    public void AFolderOutsideTheProjectIsDeclined()
    {
        string project = WriteProject("Orders");
        string outside = Path.Combine(_root, "Elsewhere");
        Directory.CreateDirectory(outside);

        Assert.False(DotSettingsWriter.SetNamespaceProvider(project, outside, isProvider: false));
    }
}

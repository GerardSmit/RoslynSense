using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services.ProjectModel;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// What happens to a file created outside the Solution Explorer, and to one copied inside it.
/// </summary>
[Collection(SharedState.Name)]
public class FileCreationAndCopyTests : IDisposable
{
    private readonly string _directory;
    private readonly string _project;

    public FileCreationAndCopyTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "roslyn-sense-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        _project = Path.Combine(_directory, "Widgets.csproj");
        File.WriteAllText(_project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <RootNamespace>Contoso.Widgets</RootNamespace>
              </PropertyGroup>
            </Project>
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task AnEmptyNewFileGetsItsNamespaceAndType()
    {
        // The editor's own explorer creates the file with no content at all.
        string path = Path.Combine(_directory, "Handlers", "OrderHandler.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "");

        string? scaffold = await ProjectMutationService.ScaffoldNewFileAsync(path, default);

        Assert.NotNull(scaffold);
        Assert.Contains("namespace Contoso.Widgets.Handlers;", scaffold);
        Assert.Contains("public class OrderHandler", scaffold);
    }

    [Fact]
    public async Task AFileWithContentIsLeftAlone()
    {
        string path = Path.Combine(_directory, "Existing.cs");
        await File.WriteAllTextAsync(path, "// mine");

        Assert.Null(await ProjectMutationService.ScaffoldNewFileAsync(path, default));
        Assert.Equal("// mine", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task GeneratedFilesAreNotScaffolded()
    {
        // Whatever generates these writes them; putting a class in first would be overwritten
        // at best and would fight the generator at worst.
        foreach (string name in new[] { "Form1.Designer.cs", "Model.g.cs", "Api.generated.cs" })
        {
            string path = Path.Combine(_directory, name);
            await File.WriteAllTextAsync(path, "");
            Assert.Null(await ProjectMutationService.ScaffoldNewFileAsync(path, default));
        }
    }

    [Fact]
    public async Task ANonCSharpFileIsNotScaffolded()
    {
        string path = Path.Combine(_directory, "appsettings.json");
        await File.WriteAllTextAsync(path, "");

        Assert.Null(await ProjectMutationService.ScaffoldNewFileAsync(path, default));
    }

    [Fact]
    public async Task CopyingAFileGivesItAFreeName()
    {
        string source = Path.Combine(_directory, "Order.cs");
        await File.WriteAllTextAsync(source, "namespace Contoso.Widgets;\n\npublic class Order { }\n");

        var first = await SolutionTreeEditHandler.EditAsync(
            Copy(source, _directory), default);
        var second = await SolutionTreeEditHandler.EditAsync(
            Copy(source, _directory), default);

        Assert.True(first.Ok, first.Message);
        Assert.True(second.Ok, second.Message);
        Assert.True(File.Exists(Path.Combine(_directory, "Order copy.cs")));
        Assert.True(File.Exists(Path.Combine(_directory, "Order copy 2.cs")));

        // The copy is the original's content: renaming the type is the user's next step, not
        // something to guess at while pasting.
        Assert.Contains(
            "public class Order",
            await File.ReadAllTextAsync(Path.Combine(_directory, "Order copy.cs")));
    }

    [Fact]
    public async Task CopyingAMissingFileReportsRatherThanThrows()
    {
        var result = await SolutionTreeEditHandler.EditAsync(
            Copy(Path.Combine(_directory, "Gone.cs"), _directory), default);

        Assert.False(result.Ok);
        Assert.Contains("no longer exists", result.Message);
    }

    private static SolutionTreeEditParams Copy(string source, string destination) =>
        new(
            Action: "copy",
            TargetUri: LspConverters.PathToUri(source),
            DestinationUri: LspConverters.PathToUri(destination));
}

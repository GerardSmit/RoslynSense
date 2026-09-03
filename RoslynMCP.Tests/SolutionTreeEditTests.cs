using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Editing from the Solution Explorer, and finding things in it without expanding first.
/// </summary>
public class SolutionTreeEditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tree-edit-{Guid.NewGuid():N}");
    private readonly string _project;

    public SolutionTreeEditTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "App"));
        _project = Path.Combine(_root, "App", "App.csproj");
        File.WriteAllText(_project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string Uri(params string[] segments) =>
        LspConverters.PathToUri(Path.Combine([Path.GetDirectoryName(_project)!, .. segments]));

    // === Creating ===

    [Fact]
    public async Task ANewFileLandsInTheFolderItWasCreatedFrom()
    {
        Directory.CreateDirectory(Path.Combine(_root, "App", "Billing"));

        var result = await SolutionTreeEditHandler.EditAsync(new SolutionTreeEditParams(
            "addFile", TargetUri: Uri("Billing"), ProjectPath: _project,
            Name: "Invoice.cs", Kind: "class"), default);

        Assert.True(result.Ok, result.Message);
        string created = Path.Combine(_root, "App", "Billing", "Invoice.cs");
        Assert.True(File.Exists(created));
        Assert.Contains("namespace App.Billing;", await File.ReadAllTextAsync(created));
    }

    [Fact]
    public async Task CreatingFromAFileNodeUsesThatFilesFolder()
    {
        // Right-clicking a file and choosing New File means "next to this one", not "at the root".
        string sibling = Path.Combine(_root, "App", "Existing.cs");
        await File.WriteAllTextAsync(sibling, "class Existing {}");

        var result = await SolutionTreeEditHandler.EditAsync(new SolutionTreeEditParams(
            "addFile", TargetUri: LspConverters.PathToUri(sibling), Name: "Added.cs"), default);

        Assert.True(result.Ok, result.Message);
        Assert.True(File.Exists(Path.Combine(_root, "App", "Added.cs")));
    }

    [Fact]
    public async Task ANewFolderIsCreated()
    {
        var result = await SolutionTreeEditHandler.EditAsync(new SolutionTreeEditParams(
            "addFolder", TargetUri: Uri(), Name: "Services"), default);

        Assert.True(result.Ok, result.Message);
        Assert.True(Directory.Exists(Path.Combine(_root, "App", "Services")));
    }

    [Fact]
    public async Task AFolderThatExistsIsNotSilentlyAccepted()
    {
        Directory.CreateDirectory(Path.Combine(_root, "App", "Services"));

        var result = await SolutionTreeEditHandler.EditAsync(new SolutionTreeEditParams(
            "addFolder", TargetUri: Uri(), Name: "Services"), default);

        Assert.False(result.Ok);
        Assert.Contains("already exists", result.Message);
    }

    // === Renaming and moving ===

    [Fact]
    public async Task RenamingMovesTheFileAndReportsItsNewUri()
    {
        string original = Path.Combine(_root, "App", "Order.cs");
        await File.WriteAllTextAsync(original, "namespace App;\n\npublic class Order\n{\n}\n");

        var result = await SolutionTreeEditHandler.EditAsync(new SolutionTreeEditParams(
            "rename", TargetUri: LspConverters.PathToUri(original), Name: "Invoice.cs"), default);

        Assert.True(result.Ok, result.Message);
        Assert.False(File.Exists(original));
        Assert.True(File.Exists(Path.Combine(_root, "App", "Invoice.cs")));
        Assert.EndsWith("Invoice.cs", result.Uri);
    }

    [Fact]
    public async Task MovingPutsTheFileInTheTargetFolder()
    {
        string source = Path.Combine(_root, "App", "Order.cs");
        await File.WriteAllTextAsync(source, "class Order {}");
        Directory.CreateDirectory(Path.Combine(_root, "App", "Billing"));

        var result = await SolutionTreeEditHandler.EditAsync(new SolutionTreeEditParams(
            "move",
            TargetUri: LspConverters.PathToUri(source),
            DestinationUri: Uri("Billing")), default);

        Assert.True(result.Ok, result.Message);
        Assert.True(File.Exists(Path.Combine(_root, "App", "Billing", "Order.cs")));
    }

    [Fact]
    public async Task DroppingAFileOnAnotherFileMovesItAlongside()
    {
        // VS Code hands over whatever node was under the cursor, which is usually a file.
        string source = Path.Combine(_root, "App", "Order.cs");
        Directory.CreateDirectory(Path.Combine(_root, "App", "Billing"));
        string neighbour = Path.Combine(_root, "App", "Billing", "Invoice.cs");
        await File.WriteAllTextAsync(source, "class Order {}");
        await File.WriteAllTextAsync(neighbour, "class Invoice {}");

        var result = await SolutionTreeEditHandler.EditAsync(new SolutionTreeEditParams(
            "move",
            TargetUri: LspConverters.PathToUri(source),
            DestinationUri: LspConverters.PathToUri(neighbour)), default);

        Assert.True(result.Ok, result.Message);
        Assert.True(File.Exists(Path.Combine(_root, "App", "Billing", "Order.cs")));
    }

    [Fact]
    public async Task MovingOntoAnExistingNameRefusesRatherThanOverwriting()
    {
        string source = Path.Combine(_root, "App", "Order.cs");
        Directory.CreateDirectory(Path.Combine(_root, "App", "Billing"));
        string occupied = Path.Combine(_root, "App", "Billing", "Order.cs");
        await File.WriteAllTextAsync(source, "class Order {}");
        await File.WriteAllTextAsync(occupied, "// the one already there");

        var result = await SolutionTreeEditHandler.EditAsync(new SolutionTreeEditParams(
            "move",
            TargetUri: LspConverters.PathToUri(source),
            DestinationUri: Uri("Billing")), default);

        Assert.False(result.Ok);
        Assert.Equal("// the one already there", await File.ReadAllTextAsync(occupied));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task RenamingToTheSameNameIsANoOp()
    {
        string original = Path.Combine(_root, "App", "Order.cs");
        await File.WriteAllTextAsync(original, "class Order {}");

        var result = await SolutionTreeEditHandler.EditAsync(new SolutionTreeEditParams(
            "rename", TargetUri: LspConverters.PathToUri(original), Name: "Order.cs"), default);

        Assert.True(result.Ok);
        Assert.True(File.Exists(original));
    }

    // === Deleting ===

    [Fact]
    public async Task DeletingAFolderTakesItsContentsWithIt()
    {
        Directory.CreateDirectory(Path.Combine(_root, "App", "Old"));
        await File.WriteAllTextAsync(Path.Combine(_root, "App", "Old", "Legacy.cs"), "class Legacy {}");

        var result = await SolutionTreeEditHandler.EditAsync(new SolutionTreeEditParams(
            "delete", TargetUri: Uri("Old")), default);

        Assert.True(result.Ok, result.Message);
        Assert.False(Directory.Exists(Path.Combine(_root, "App", "Old")));
    }

    [Fact]
    public async Task AnUnknownActionIsRejectedByName()
    {
        var result = await SolutionTreeEditHandler.EditAsync(
            new SolutionTreeEditParams("teleport"), default);

        Assert.False(result.Ok);
        Assert.Contains("teleport", result.Message);
    }
}

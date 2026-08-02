using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>workspace/willRenameFiles: renaming a file renames the type it declares, and the
/// references that go with it.</summary>
[Collection(SharedState.Name)]
public class FileOperationsTests : IAsyncLifetime
{
    private string _typePath = "";
    private string _userPath = "";

    public async Task InitializeAsync()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        _typePath = Path.Combine(FixturePaths.SampleProjectDir, $"RenameSubject{suffix}.cs");
        _userPath = Path.Combine(FixturePaths.SampleProjectDir, $"RenameUser{suffix}.cs");

        await File.WriteAllTextAsync(_typePath, $$"""
            namespace SampleProject;

            public class RenameSubject{{suffix}}
            {
                public int Value => 1;
            }
            """);
        await File.WriteAllTextAsync(_userPath, $$"""
            namespace SampleProject;

            public class RenameUser{{suffix}}
            {
                public int Use() => new RenameSubject{{suffix}}().Value;
            }
            """);

        await WorkspaceService.EvictAllAsync();
    }

    public async Task DisposeAsync()
    {
        File.Delete(_typePath);
        File.Delete(_userPath);
        await WorkspaceService.EvictAllAsync();
    }

    [Fact]
    public async Task RenamingAFileRenamesItsTypeAndEveryReference()
    {
        string newPath = Path.Combine(
            FixturePaths.SampleProjectDir,
            "Renamed" + Path.GetFileName(_typePath));

        var edit = await FileOperationsHandler.WillRenameAsync(
            new RenameFilesParams([new FileRename(
                LspConverters.PathToUri(_typePath), LspConverters.PathToUri(newPath))]),
            default);

        Assert.NotNull(edit);
        // The declaration and the use site in the other file both move.
        Assert.Contains(edit!.Changes, c => c.Key.EndsWith(Path.GetFileName(_typePath), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(edit.Changes, c => c.Key.EndsWith(Path.GetFileName(_userPath), StringComparison.OrdinalIgnoreCase));
        // Roslyn emits minimal diffs, so an individual edit's text can be a fragment like "d" —
        // asserting on it would test the differ, not us. Our contract is that both the
        // declaration and the reference get edits.
        Assert.All(edit.Changes.Values, edits => Assert.NotEmpty(edits));
    }

    [Fact]
    public async Task RenamingToAnInvalidIdentifierProducesNoEdit()
    {
        string newPath = Path.Combine(FixturePaths.SampleProjectDir, "not-an-identifier.cs");

        var edit = await FileOperationsHandler.WillRenameAsync(
            new RenameFilesParams([new FileRename(
                LspConverters.PathToUri(_typePath), LspConverters.PathToUri(newPath))]),
            default);

        Assert.Null(edit);
    }

    [Fact]
    public async Task RenamingAFileWhoseNameNeverMatchedATypeIsLeftAlone()
    {
        // Nothing in the project declares a type called Calculator2, so there is nothing to
        // rename — guessing would rewrite code the user did not ask about.
        var edit = await FileOperationsHandler.WillRenameAsync(
            new RenameFilesParams([new FileRename(
                LspConverters.PathToUri(Path.Combine(FixturePaths.SampleProjectDir, "Services.cs")),
                LspConverters.PathToUri(Path.Combine(FixturePaths.SampleProjectDir, "Calculator2.cs")))]),
            default);

        Assert.Null(edit);
    }

    [Fact]
    public async Task NonSourceFilesAreIgnored()
    {
        var edit = await FileOperationsHandler.WillRenameAsync(
            new RenameFilesParams([new FileRename(
                LspConverters.PathToUri(Path.Combine(FixturePaths.SampleProjectDir, "readme.md")),
                LspConverters.PathToUri(Path.Combine(FixturePaths.SampleProjectDir, "guide.md")))]),
            default);

        Assert.Null(edit);
    }
}

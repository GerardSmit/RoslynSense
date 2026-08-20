using RoslynMCP.Lsp;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Which project compiles a file is remembered; whether one does is not.
/// </summary>
/// <remarks>
/// <see cref="WorkspaceService.FindContainingProjectAsync"/> walks from the file up towards the
/// drive root, enumerating every ancestor directory for <c>*.csproj</c> and opening each candidate
/// it passes to ask whether that project holds the file. It sits under
/// <see cref="LspDocumentResolver.ResolveAsync"/>, which is the first line of hover, completion,
/// signature help, semantic tokens, folding, inlay hints, code lens, formatting, rename and every
/// navigation request — so the walk ran several times per keystroke.
/// </remarks>
[Collection(SharedState.Name)]
public class ContainingProjectMemoTests
{
    [Fact]
    public async Task TheSecondResolveOfAFileDoesNotWalkTheTreeAgain()
    {
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);
        WorkspaceService.ResetContainingProjectMemo();

        var first = await LspDocumentResolver.ResolveAsync(FixturePaths.CalculatorFile, default);
        Assert.NotNull(first);
        Assert.Equal(1L, WorkspaceService.ProjectSearches);

        var second = await LspDocumentResolver.ResolveAsync(FixturePaths.CalculatorFile, default);

        Assert.NotNull(second);
        Assert.Equal(first!.Id, second!.Id);
        Assert.Equal(1L, WorkspaceService.ProjectSearches);
    }

    /// <summary>
    /// And a remembered owner is still checked rather than believed: the answer comes from asking
    /// that project for the file, so an entry that has gone wrong costs a lookup and not a wrong
    /// document.
    /// </summary>
    [Fact]
    public async Task ARememberedOwnerStillAnswersWithTheCurrentDocument()
    {
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);
        WorkspaceService.ResetContainingProjectMemo();

        var cold = await LspDocumentResolver.ResolveAsync(FixturePaths.CalculatorFile, default);
        var warm = await LspDocumentResolver.ResolveAsync(FixturePaths.CalculatorFile, default);

        Assert.NotNull(cold);
        Assert.NotNull(warm);

        string coldText = (await cold!.GetTextAsync()).ToString();
        string warmText = (await warm!.GetTextAsync()).ToString();
        Assert.Equal(coldText, warmText);
        Assert.Equal(cold.Project.FilePath, warm.Project.FilePath);
    }

    /// <summary>
    /// A file that no project compiles is looked for again every time.
    /// </summary>
    /// <remarks>
    /// This is the entry it is most tempting to keep — the miss is the case that walks all the way
    /// to the root — and the one it is least safe to keep. A file created on disk belongs to no
    /// project until the watcher syncs it in, and an MCP-only session has no watcher at all, so a
    /// remembered "nothing owns this" would outlive the creation and leave the file inert with no
    /// diagnostics, no hover and no navigation for the rest of the session.
    /// </remarks>
    [Fact]
    public async Task AFileNoProjectCompilesIsNeverRemembered()
    {
        string root = Path.Combine(Path.GetTempPath(), "roslynsense-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string orphan = Path.Combine(root, "Orphan.cs");
            await File.WriteAllTextAsync(orphan, "class Orphan { }");

            WorkspaceService.ResetContainingProjectMemo();

            Assert.Null(await WorkspaceService.FindContainingProjectAsync(orphan, default));
            long afterFirst = WorkspaceService.ProjectSearches;

            Assert.Null(await WorkspaceService.FindContainingProjectAsync(orphan, default));

            Assert.Equal(afterFirst + 1, WorkspaceService.ProjectSearches);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }
}

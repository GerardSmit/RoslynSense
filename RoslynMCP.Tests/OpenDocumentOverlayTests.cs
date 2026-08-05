using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

[Collection(SharedState.Name)]
public class OpenDocumentOverlayTests
{
    [Fact]
    public async Task SnapshotSeesOpenBufferTextAndRevertsOnClose()
    {
        string path = FixturePaths.CalculatorFile;
        string diskText = await File.ReadAllTextAsync(path);
        string bufferText = diskText + "\n// overlay-marker: unsaved editor edit\n";
        string session = Guid.NewGuid().ToString("N");

        OpenDocumentStore.Open(session, path, SourceText.From(bufferText), version: 1);
        try
        {
            var text = await GetSnapshotTextAsync(path);
            Assert.Contains("overlay-marker", text);
        }
        finally
        {
            OpenDocumentStore.Close(session, path);
        }

        var afterClose = await GetSnapshotTextAsync(path);
        Assert.DoesNotContain("overlay-marker", afterClose);
    }

    [Fact]
    public async Task OverlayAppliesToAllOpenDocumentsNotJustTarget()
    {
        // The overlay must cover every open buffer, so a request targeting file A still sees
        // unsaved edits in open file B (cross-file analysis correctness).
        string targetPath = FixturePaths.CalculatorFile;
        string otherPath = FixturePaths.ServicesFile;
        string otherDisk = await File.ReadAllTextAsync(otherPath);
        string session = Guid.NewGuid().ToString("N");

        OpenDocumentStore.Open(session, otherPath,
            SourceText.From(otherDisk + "\n// other-overlay-marker\n"), version: 1);
        try
        {
            var projectPath = await WorkspaceService.FindContainingProjectAsync(targetPath, default);
            Assert.NotNull(projectPath);
            var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
                projectPath!, targetFilePath: targetPath, cancellationToken: default);

            var otherDoc = WorkspaceService.FindDocumentInProject(project, otherPath);
            Assert.NotNull(otherDoc);
            var otherText = (await otherDoc!.GetTextAsync()).ToString();
            Assert.Contains("other-overlay-marker", otherText);
        }
        finally
        {
            OpenDocumentStore.Close(session, otherPath);
        }
    }

    [Fact]
    public void CloseSessionDropsOnlyDocumentsWithNoRemainingOwner()
    {
        string path = FixturePaths.CalculatorFile;
        var text = SourceText.From("// shared");

        OpenDocumentStore.Open("session-a", path, text, 1);
        OpenDocumentStore.Open("session-b", path, text, 1);

        OpenDocumentStore.CloseSession("session-a");
        Assert.True(OpenDocumentStore.IsOpen(path)); // session-b still owns it

        OpenDocumentStore.CloseSession("session-b");
        Assert.False(OpenDocumentStore.IsOpen(path));
    }

    /// <summary>
    /// Opening a markup file must not fork the solution.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An <c>.ascx</c> is not a Roslyn document, so the overlay can never apply it — but the store
    /// bumped one generation for every buffer, and the rebuild that followed produced a new
    /// <see cref="Microsoft.CodeAnalysis.Solution"/> whether or not any text actually moved. Every
    /// compilation went with it, and so did everything keyed on one: the memoized markup parses
    /// (which compare compilations by reference), and every document's dependent semantic version,
    /// which is half of the <c>workspace/diagnostic</c> result id. Closing and reopening one page
    /// therefore made the next pull report a whole website as changed and re-analyse it.
    /// </para>
    /// <para>
    /// Asserted on the compilation rather than on the counter, because the counter is the mechanism
    /// and this is the property that matters — a later mechanism that keeps the snapshot stable
    /// should keep this test passing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task OpeningAMarkupBufferLeavesTheCompilationSnapshotAlone()
    {
        string session = Guid.NewGuid().ToString("N");
        string markupPath = FixturePaths.DefaultAspxFile;

        var before = await GetCompilationAsync(FixturePaths.AspxProjectFile);

        OpenDocumentStore.Open(
            session, markupPath, SourceText.From(await File.ReadAllTextAsync(markupPath)), version: 1);
        try
        {
            Assert.Same(before, await GetCompilationAsync(FixturePaths.AspxProjectFile));
        }
        finally
        {
            OpenDocumentStore.Close(session, markupPath);
        }

        // And closing it again is the other half of the reported gesture.
        Assert.Same(before, await GetCompilationAsync(FixturePaths.AspxProjectFile));
    }

    private static async Task<Microsoft.CodeAnalysis.Compilation?> GetCompilationAsync(string projectPath)
    {
        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
            projectPath, cancellationToken: default);
        return await project.GetCompilationAsync();
    }

    private static async Task<string> GetSnapshotTextAsync(string filePath)
    {
        var projectPath = await WorkspaceService.FindContainingProjectAsync(filePath, default);
        Assert.NotNull(projectPath);
        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
            projectPath!, targetFilePath: filePath, cancellationToken: default);
        var document = WorkspaceService.FindDocumentInProject(project, filePath);
        Assert.NotNull(document);
        return (await document!.GetTextAsync()).ToString();
    }
}

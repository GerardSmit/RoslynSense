using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

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

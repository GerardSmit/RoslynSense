using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

public class WorkspaceServiceTests
{
    [Fact]
    public async Task WhenMultipleProjectFilesExistThenContainingProjectResolutionUsesActualOwner()
    {
        await WorkspaceService.EvictAllAsync();

        string? projectPath = await WorkspaceService.FindContainingProjectAsync(FixturePaths.CalculatorFile);

        Assert.Equal(
            Path.GetFullPath(FixturePaths.SampleProjectFile),
            Path.GetFullPath(projectPath!),
            ignoreCase: true);
    }

    [Fact]
    public async Task WhenSameProjectOpenedTwiceThenCachedWorkspaceIsReused()
    {
        await WorkspaceService.EvictAllAsync();

        var first = await WorkspaceService.GetOrOpenProjectAsync(
            FixturePaths.SampleProjectFile,
            targetFilePath: FixturePaths.CalculatorFile);
        var second = await WorkspaceService.GetOrOpenProjectAsync(
            FixturePaths.SampleProjectFile,
            targetFilePath: FixturePaths.CalculatorFile);

        Assert.Same(first.Workspace, second.Workspace);
        Assert.Equal(first.Project.Id, second.Project.Id);
    }

    [Fact]
    public async Task WhenProjectTimestampChangesThenCachedWorkspaceIsInvalidated()
    {
        await WorkspaceService.EvictAllAsync();

        DateTime originalWriteTime = File.GetLastWriteTimeUtc(FixturePaths.SampleProjectFile);
        var first = await WorkspaceService.GetOrOpenProjectAsync(
            FixturePaths.SampleProjectFile,
            targetFilePath: FixturePaths.CalculatorFile);

        try
        {
            File.SetLastWriteTimeUtc(FixturePaths.SampleProjectFile, DateTime.UtcNow.AddMinutes(5));

            var second = await WorkspaceService.GetOrOpenProjectAsync(
                FixturePaths.SampleProjectFile,
                targetFilePath: FixturePaths.CalculatorFile);

            Assert.NotSame(first.Workspace, second.Workspace);
        }
        finally
        {
            File.SetLastWriteTimeUtc(FixturePaths.SampleProjectFile, originalWriteTime);
            await WorkspaceService.EvictAllAsync();
        }
    }

    [Fact]
    public async Task WhenFileInSubdirectoryThenFindContainingProjectLocatesProject()
    {
        // Result.cs is in Models/ subdirectory
        string? projectPath = await WorkspaceService.FindContainingProjectAsync(FixturePaths.ResultFile);

        Assert.NotNull(projectPath);
        Assert.Equal(
            Path.GetFullPath(FixturePaths.SampleProjectFile),
            Path.GetFullPath(projectPath!),
            ignoreCase: true);
    }

    [Fact]
    public async Task WhenDocumentSearchedThenFindDocumentLocatesFile()
    {
        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
            FixturePaths.SampleProjectFile,
            targetFilePath: FixturePaths.CalculatorFile);

        var document = WorkspaceService.FindDocumentInProject(project, FixturePaths.CalculatorFile);

        Assert.NotNull(document);
        Assert.Contains("Calculator.cs", document!.FilePath!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenNonExistentDocumentSearchedThenReturnsNull()
    {
        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
            FixturePaths.SampleProjectFile,
            targetFilePath: FixturePaths.CalculatorFile);

        var document = WorkspaceService.FindDocumentInProject(
            project, Path.Combine(FixturePaths.SampleProjectDir, "Ghost.cs"));

        Assert.Null(document);
    }

    [Fact]
    public async Task WhenBrokenProjectOpenedThenStillReturnsProject()
    {
        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
            FixturePaths.BrokenProjectFile);

        Assert.NotNull(project);
        Assert.Contains("BrokenProject", project.Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenSourceFileModifiedAfterCacheThenDocumentTextIsRefreshed()
    {
        await WorkspaceService.EvictAllAsync();

        // Use dedicated file so other parallel tests aren't affected
        string originalContent = await File.ReadAllTextAsync(FixturePaths.WorkspaceRefreshTargetFile);
        string modifiedContent = originalContent.Replace(
            "public int Compute(int x) => x * 2;",
            "public int ComputeModified(int x) => x * 2;");

        Assert.NotEqual(originalContent, modifiedContent); // guard: replacement actually happened

        try
        {
            // Populate cache
            await WorkspaceService.GetOrOpenProjectAsync(
                FixturePaths.SampleProjectFile,
                targetFilePath: FixturePaths.WorkspaceRefreshTargetFile);

            // Write modified content and advance the file timestamp past cache time
            await File.WriteAllTextAsync(FixturePaths.WorkspaceRefreshTargetFile, modifiedContent);
            File.SetLastWriteTimeUtc(FixturePaths.WorkspaceRefreshTargetFile, DateTime.UtcNow.AddMinutes(5));

            // Re-query with the changed file as targetFilePath
            var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
                FixturePaths.SampleProjectFile,
                targetFilePath: FixturePaths.WorkspaceRefreshTargetFile);

            var document = WorkspaceService.FindDocumentInProject(project, FixturePaths.WorkspaceRefreshTargetFile);
            Assert.NotNull(document);

            var text = (await document!.GetTextAsync()).ToString();
            Assert.Contains("ComputeModified", text);
            Assert.DoesNotContain("public int Compute(int", text);
        }
        finally
        {
            await File.WriteAllTextAsync(FixturePaths.WorkspaceRefreshTargetFile, originalContent);
            await WorkspaceService.EvictAllAsync();
        }
    }

    [Fact]
    public async Task WhenUnrelatedFileModifiedThenCachedContentIsUsed()
    {
        await WorkspaceService.EvictAllAsync();

        // Modify the dedicated file but query a different file as targetFilePath.
        // The modified file should NOT be refreshed in the returned snapshot.
        string originalContent = await File.ReadAllTextAsync(FixturePaths.WorkspaceRefreshTargetFile);
        string modifiedContent = originalContent + "\n// sentinel-change";

        try
        {
            // Populate cache via CalculatorFile (not the file we'll modify)
            await WorkspaceService.GetOrOpenProjectAsync(
                FixturePaths.SampleProjectFile,
                targetFilePath: FixturePaths.CalculatorFile);

            // Modify WorkspaceRefreshTargetFile and advance its timestamp
            await File.WriteAllTextAsync(FixturePaths.WorkspaceRefreshTargetFile, modifiedContent);
            File.SetLastWriteTimeUtc(FixturePaths.WorkspaceRefreshTargetFile, DateTime.UtcNow.AddMinutes(5));

            // Re-query with CalculatorFile as targetFilePath — WorkspaceRefreshTargetFile should NOT refresh
            var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
                FixturePaths.SampleProjectFile,
                targetFilePath: FixturePaths.CalculatorFile);

            var doc = WorkspaceService.FindDocumentInProject(project, FixturePaths.WorkspaceRefreshTargetFile);
            Assert.NotNull(doc);

            var text = (await doc!.GetTextAsync()).ToString();
            Assert.DoesNotContain("sentinel-change", text);
        }
        finally
        {
            await File.WriteAllTextAsync(FixturePaths.WorkspaceRefreshTargetFile, originalContent);
            await WorkspaceService.EvictAllAsync();
        }
    }
}

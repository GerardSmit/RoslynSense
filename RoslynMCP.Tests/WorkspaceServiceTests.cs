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

        string originalContent = await File.ReadAllTextAsync(FixturePaths.CalculatorFile);
        string modifiedContent = originalContent.Replace(
            "public int Add(int a, int b) => a + b;",
            "public int AddModified(int a, int b) => a + b;");

        Assert.NotEqual(originalContent, modifiedContent); // guard: replacement actually happened

        try
        {
            // Populate cache
            await WorkspaceService.GetOrOpenProjectAsync(
                FixturePaths.SampleProjectFile,
                targetFilePath: FixturePaths.CalculatorFile);

            // Write modified content and advance the file timestamp past cache time
            await File.WriteAllTextAsync(FixturePaths.CalculatorFile, modifiedContent);
            File.SetLastWriteTimeUtc(FixturePaths.CalculatorFile, DateTime.UtcNow.AddMinutes(5));

            // Re-query with the changed file as targetFilePath
            var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
                FixturePaths.SampleProjectFile,
                targetFilePath: FixturePaths.CalculatorFile);

            var document = WorkspaceService.FindDocumentInProject(project, FixturePaths.CalculatorFile);
            Assert.NotNull(document);

            var text = (await document!.GetTextAsync()).ToString();
            Assert.Contains("AddModified", text);
            Assert.DoesNotContain("public int Add(int", text);
        }
        finally
        {
            await File.WriteAllTextAsync(FixturePaths.CalculatorFile, originalContent);
            await WorkspaceService.EvictAllAsync();
        }
    }

    [Fact]
    public async Task WhenUnrelatedFileModifiedThenCachedContentIsUsed()
    {
        await WorkspaceService.EvictAllAsync();

        string originalServices = await File.ReadAllTextAsync(FixturePaths.ServicesFile);
        string modifiedServices = originalServices + "\n// sentinel-change";

        try
        {
            // Populate cache by loading Calculator.cs
            await WorkspaceService.GetOrOpenProjectAsync(
                FixturePaths.SampleProjectFile,
                targetFilePath: FixturePaths.CalculatorFile);

            // Modify Services.cs (not the targetFilePath) and advance its timestamp
            await File.WriteAllTextAsync(FixturePaths.ServicesFile, modifiedServices);
            File.SetLastWriteTimeUtc(FixturePaths.ServicesFile, DateTime.UtcNow.AddMinutes(5));

            // Re-query with Calculator.cs as targetFilePath — Services.cs should NOT be refreshed
            var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
                FixturePaths.SampleProjectFile,
                targetFilePath: FixturePaths.CalculatorFile);

            var servicesDoc = WorkspaceService.FindDocumentInProject(project, FixturePaths.ServicesFile);
            Assert.NotNull(servicesDoc);

            var text = (await servicesDoc!.GetTextAsync()).ToString();
            Assert.DoesNotContain("sentinel-change", text);
        }
        finally
        {
            await File.WriteAllTextAsync(FixturePaths.ServicesFile, originalServices);
            await WorkspaceService.EvictAllAsync();
        }
    }
}

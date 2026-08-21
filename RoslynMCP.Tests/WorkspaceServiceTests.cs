using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

[Collection(SharedState.Name)]
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

    /// <remarks>
    /// The pair to <see cref="WhenSourceFileModifiedAfterCacheThenDocumentTextIsRefreshed"/>: that
    /// one proves the requested file <em>is</em> refreshed, this one proves the refresh is scoped
    /// to it rather than re-stat-ing and reloading every document in the project on every query.
    /// <para>
    /// The materialising read below is load-bearing and used not to be. Loading a project used to
    /// force a full <c>GetCompilationAsync</c> for every project, which parsed every document and
    /// therefore snapshotted every document's text as a side effect — so this test could modify a
    /// file and observe the old text without ever having asked for it. That compilation was removed
    /// (it existed to answer a question about metadata references and cost the parse of an entire
    /// codebase at load time), and without it Roslyn's <c>FileTextLoader</c> stays lazy: a document
    /// nothing has read yet reads whatever is on disk when it is first asked, which is neither a
    /// refresh nor a cache hit. Reading the text first makes the precondition explicit instead of
    /// inherited from an unrelated implementation detail.
    /// </para>
    /// </remarks>
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
            var (_, populated) = await WorkspaceService.GetOrOpenProjectAsync(
                FixturePaths.SampleProjectFile,
                targetFilePath: FixturePaths.CalculatorFile);

            // Materialise the unrelated file's text into the cached workspace, so that what follows
            // measures whether the re-query refreshes it — see the remarks above.
            var cachedDoc = WorkspaceService.FindDocumentInProject(
                populated, FixturePaths.WorkspaceRefreshTargetFile);
            Assert.NotNull(cachedDoc);
            Assert.DoesNotContain("sentinel-change", (await cachedDoc!.GetTextAsync()).ToString());

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

    /// <summary>
    /// A file that changed on disk is refreshed into a fork once, not once per request.
    /// </summary>
    /// <remarks>
    /// The refresh forks the solution, and the fork used to be discarded with the request that
    /// made it — so the next request replayed the tree replace and re-bound the project, and did so
    /// forever after, for a file that had long since stopped changing. Identity of the
    /// <see cref="Solution"/> is the whole assertion: every downstream cache that survives —
    /// semantic models, the frozen-partial memo — hangs off that instance and nothing weaker.
    /// </remarks>
    [Fact]
    public async Task WhenSourceFileUnchangedSinceLastRefreshThenTheSameSolutionIsReturned()
    {
        await WorkspaceService.EvictAllAsync();

        string originalContent = await File.ReadAllTextAsync(FixturePaths.WorkspaceRefreshTargetFile);

        try
        {
            await WorkspaceService.GetOrOpenProjectAsync(
                FixturePaths.SampleProjectFile,
                targetFilePath: FixturePaths.WorkspaceRefreshTargetFile);

            await File.WriteAllTextAsync(
                FixturePaths.WorkspaceRefreshTargetFile, originalContent + "\n// first edit");
            File.SetLastWriteTimeUtc(FixturePaths.WorkspaceRefreshTargetFile, DateTime.UtcNow.AddMinutes(5));

            var first = await WorkspaceService.GetOrOpenProjectAsync(
                FixturePaths.SampleProjectFile,
                targetFilePath: FixturePaths.WorkspaceRefreshTargetFile);
            var second = await WorkspaceService.GetOrOpenProjectAsync(
                FixturePaths.SampleProjectFile,
                targetFilePath: FixturePaths.WorkspaceRefreshTargetFile);

            // Guard: the fork really happened, so identity below is not identity with the base.
            Assert.Contains("first edit", (await TargetTextAsync(first.Project)));
            Assert.Same(first.Project.Solution, second.Project.Solution);

            // And the memo is keyed on the bytes, not on having refreshed once.
            await File.WriteAllTextAsync(
                FixturePaths.WorkspaceRefreshTargetFile, originalContent + "\n// second edit");
            File.SetLastWriteTimeUtc(FixturePaths.WorkspaceRefreshTargetFile, DateTime.UtcNow.AddMinutes(5));

            var third = await WorkspaceService.GetOrOpenProjectAsync(
                FixturePaths.SampleProjectFile,
                targetFilePath: FixturePaths.WorkspaceRefreshTargetFile);

            Assert.NotSame(second.Project.Solution, third.Project.Solution);
            Assert.Contains("second edit", (await TargetTextAsync(third.Project)));
        }
        finally
        {
            await File.WriteAllTextAsync(FixturePaths.WorkspaceRefreshTargetFile, originalContent);
            await WorkspaceService.EvictAllAsync();
        }
    }

    private static async Task<string> TargetTextAsync(Microsoft.CodeAnalysis.Project project)
    {
        var document = WorkspaceService.FindDocumentInProject(
            project, FixturePaths.WorkspaceRefreshTargetFile);
        Assert.NotNull(document);
        return (await document!.GetTextAsync()).ToString();
    }
}

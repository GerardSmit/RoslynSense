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
    /// one proves the requested file is refreshed, this one proves a file the request did
    /// <em>not</em> name is refreshed too. It used to assert the opposite — refresh was scoped to
    /// the named file so that no query paid a stat per document — but an MCP client edits files it
    /// never names in the next question: diagnostics asked of one file answered with another
    /// file's load-time text, at load-time line numbers. The workspace's directory watcher
    /// (<c>WorkspaceDirtyWatcher</c>) is what reconciles the two costs: nothing is stat-ed unless
    /// the file system itself said it changed.
    /// <para>
    /// The materialising read below is load-bearing: Roslyn's <c>FileTextLoader</c> stays lazy,
    /// and a document nothing has read yet reads whatever is on disk when first asked — which is
    /// neither a refresh nor a cache hit. Reading first makes the precondition explicit. The
    /// re-query polls briefly because the watcher's event is delivered asynchronously; the named
    /// file needs no such grace only because it is stat-ed directly.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task WhenAFileTheRequestDoesNotNameChangesOnDiskItIsRefreshedToo()
    {
        await WorkspaceService.EvictAllAsync();

        string originalContent = await File.ReadAllTextAsync(FixturePaths.WorkspaceRefreshTargetFile);
        string modifiedContent = originalContent + "\n// sentinel-change";

        try
        {
            // Populate cache via CalculatorFile (not the file we'll modify)
            var (_, populated) = await WorkspaceService.GetOrOpenProjectAsync(
                FixturePaths.SampleProjectFile,
                targetFilePath: FixturePaths.CalculatorFile);

            var cachedDoc = WorkspaceService.FindDocumentInProject(
                populated, FixturePaths.WorkspaceRefreshTargetFile);
            Assert.NotNull(cachedDoc);
            Assert.DoesNotContain("sentinel-change", (await cachedDoc!.GetTextAsync()).ToString());

            await File.WriteAllTextAsync(FixturePaths.WorkspaceRefreshTargetFile, modifiedContent);
            File.SetLastWriteTimeUtc(FixturePaths.WorkspaceRefreshTargetFile, DateTime.UtcNow.AddMinutes(5));

            // Re-query with CalculatorFile as targetFilePath: the modified file must show up
            // fresh regardless, once its watcher event has landed.
            string text = "";
            for (int attempt = 0; attempt < 100; attempt++)
            {
                var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
                    FixturePaths.SampleProjectFile,
                    targetFilePath: FixturePaths.CalculatorFile);

                var doc = WorkspaceService.FindDocumentInProject(
                    project, FixturePaths.WorkspaceRefreshTargetFile);
                Assert.NotNull(doc);

                text = (await doc!.GetTextAsync()).ToString();
                if (text.Contains("sentinel-change", StringComparison.Ordinal))
                    break;
                await Task.Delay(50);
            }

            Assert.Contains("sentinel-change", text);
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

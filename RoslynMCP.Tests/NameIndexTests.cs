using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Search;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The names read straight off disk: the corpus Search Everywhere answers from during the seconds
/// MSBuild is still evaluating the solution.
/// </summary>
/// <remarks>
/// The whole point is what these tests do <em>not</em> do — they never open a workspace, never load
/// a project, and never touch <see cref="WorkspaceService"/>. If any of that were required, the
/// index would not be able to do its job, which is to answer before any of it has happened.
/// </remarks>
[Collection(SharedState.Name)]
public class NameIndexTests
{
    [Fact]
    public async Task ATypeIsFoundWithNoProjectLoaded()
    {
        var index = await BuildAsync();

        var hits = SearchEverywhere.SearchNames(index, "Caller", maxResults: 50, default);

        Assert.Contains(hits, hit =>
            hit.Kind == SearchItemKind.Type
            && hit.Name == "Caller"
            && hit.FilePath.Contains("ProjectB", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ADeclarationCarriesThePositionItWasFoundAt()
    {
        var index = await BuildAsync();

        var hit = Assert.Single(
            SearchEverywhere.SearchNames(index, "Caller", maxResults: 50, default),
            h => h.Kind == SearchItemKind.Type && h.Name == "Caller");

        // A row that opens the file at the wrong place is worse than no row: the position comes
        // from the parsed span, so it has to survive being turned into a line and back.
        string[] lines = await File.ReadAllLinesAsync(hit.FilePath);
        Assert.Contains("Caller", lines[hit.Line]);
    }

    [Fact]
    public async Task FilesAreSearchableByNameToo()
    {
        var index = await BuildAsync();

        var hits = SearchEverywhere.SearchNames(index, "Class2.cs", maxResults: 50, default);

        Assert.Contains(hits, hit => hit.Kind == SearchItemKind.File && hit.Name == "Class2.cs");
    }

    [Fact]
    public async Task AKindFilterNarrowsTheSameWayItDoesOnTheLoadedSolution()
    {
        var index = await BuildAsync();

        // The tabs are the filter: Classes must never answer with a method, and Symbols must never
        // answer with a type, however well the name matches.
        var hits = SearchEverywhere.SearchNames(
            index, "Caller", maxResults: 50, default, only: SearchItemKind.Type);

        Assert.NotEmpty(hits);
        Assert.All(hits, hit => Assert.Equal(SearchItemKind.Type, hit.Kind));

        var members = SearchEverywhere.SearchNames(
            index, "Run", maxResults: 50, default, only: SearchItemKind.Member);

        Assert.Contains(members, hit => hit.Name == "Run");
        Assert.All(members, hit => Assert.Equal(SearchItemKind.Member, hit.Kind));
    }

    /// <summary>
    /// The cache is what makes reopening a solution cheap; a round trip that loses a declaration
    /// would make the second open of a solution quietly worse than the first.
    /// </summary>
    [Fact]
    public async Task WhatIsWrittenToDiskIsWhatComesBack()
    {
        var index = await BuildAsync();
        string solution = Path.Combine(Path.GetTempPath(), $"name-index-test-{Guid.NewGuid():N}.sln");

        try
        {
            NameIndexStore.TryWrite(solution, index.Sources);
            var restored = NameIndexStore.TryRead(solution);

            Assert.NotNull(restored);
            Assert.Equal(index.Sources.Count, restored.Count);

            foreach (var source in index.Sources)
            {
                var back = restored[source.Path];
                Assert.Equal(source.Length, back.Length);
                Assert.Equal(source.ModifiedUtcTicks, back.ModifiedUtcTicks);
                Assert.Equal(source.Declarations, back.Declarations);
            }
        }
        finally
        {
            try { File.Delete(NameIndexStore.PathFor(solution)); } catch { }
        }
    }

    /// <summary>
    /// The index is a stand-in, and a stand-in that outlives the thing it stands in for is a bug:
    /// it stops being maintained the moment it is built, so a search that could read the loaded
    /// solution must never be answered from it.
    /// </summary>
    [Fact]
    public async Task AnIndexIsNotOfferedOnceTheSolutionIsLoaded()
    {
        NameIndex.Reset();
        await NameIndex.Start(FixturePaths.MultiSolutionFile);

        Assert.Null(await NameIndex.ReadyBeforeAsync(Task.CompletedTask, default));

        NameIndex.Reset();
    }

    [Theory]
    [InlineData("retire")]
    [InlineData("reset")]
    [InlineData("replace")]
    public async Task AbandonedBuildCancelsItsParseAndPreservesThePreviousDiskIndex(string action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "name-index-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string solution = Path.Combine(directory, "First.slnx");
        string replacement = Path.Combine(directory, "Second.slnx");
        string source = Path.Combine(directory, "Names.cs");
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<NameIndexSnapshot?>? oldBuild = null, newBuild = null;
        NameIndex.Reset();

        try
        {
            await File.WriteAllTextAsync(solution, "<Solution />");
            await File.WriteAllTextAsync(replacement, "<Solution />");
            await File.WriteAllTextAsync(source, "public class CurrentName { }");
            // This previous entry is deliberately stale. Cancellation must leave its complete
            // persisted snapshot intact, rather than overwrite it with partially collected names.
            NameIndexStore.TryWrite(solution, [new NameSource(source, -1, 0, [])]);
            byte[] previous = await File.ReadAllBytesAsync(NameIndexStore.PathFor(solution));
            oldBuild = NameIndex.StartForTestAsync(solution, async ct =>
            {
                entered.TrySetResult(ct);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            });
            var parseToken = await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(parseToken.IsCancellationRequested);

            if (action == "retire")
                NameIndex.Retire();
            else if (action == "reset")
                NameIndex.Reset();
            else
                newBuild = NameIndex.StartForTestAsync(replacement,
                    ct => Task.Delay(Timeout.InfiniteTimeSpan, ct));

            Assert.Null(await oldBuild.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.True(parseToken.IsCancellationRequested);
            Assert.True(oldBuild.IsCompletedSuccessfully);
            Assert.Equal(previous, await File.ReadAllBytesAsync(NameIndexStore.PathFor(solution)));
            if (action == "retire")
                Assert.Null(await NameIndex.Start(solution)); // Retirement also prevents rebuilding.
        }
        finally
        {
            NameIndex.Reset();
            if (oldBuild is not null)
                await oldBuild.WaitAsync(TimeSpan.FromSeconds(30));
            if (newBuild is not null)
                await newBuild.WaitAsync(TimeSpan.FromSeconds(30));
            try { File.Delete(NameIndexStore.PathFor(solution)); } catch { }
            try { File.Delete(NameIndexStore.PathFor(replacement)); } catch { }
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private static async Task<NameIndexSnapshot> BuildAsync()
    {
        // Never persisted: a test that wrote the cache would hand its fixture's state to the next
        // run, and to whatever session is using the same machine.
        var index = await NameIndex.BuildForTestAsync(FixturePaths.MultiSolutionFile, persist: false);
        Assert.NotNull(index);
        return index;
    }
}

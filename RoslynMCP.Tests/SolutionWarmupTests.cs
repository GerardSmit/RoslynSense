using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Config;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The eager solution load behind Search Everywhere: with it, the whole solution is searchable
/// from the moment the server is up; without it, only the projects some open file dragged in.
/// </summary>
[Collection(SharedState.Name)]
public class SolutionWarmupTests
{
    [Fact]
    public void BindingNullClearsTheCurrentSolution()
    {
        using var restore = WorkspaceService.BindSolutionForTesting(FixturePaths.MultiSolutionFile);

        WorkspaceService.BindSolution(null);

        Assert.Null(WorkspaceService.BoundSolutionPath);
    }

    /// <summary>
    /// ProjectB references ProjectA and nothing references ProjectB, so ProjectB is exactly what
    /// demand-driven loading never reaches on its own: opening a file in ProjectA pulls in its
    /// closure, which does not include its consumer. Finding <c>Caller</c> with no document open
    /// at all is therefore the whole feature in one assertion.
    /// </summary>
    [Fact]
    public async Task EveryProjectIsSearchableWithoutOpeningAFile()
    {
        string? previous = WorkspaceService.BoundSolutionPath;
        bool previousSetting = LspFeatureOptions.LoadEntireSolution;

        try
        {
            await WorkspaceService.EvictAllAsync();
            LspFeatureOptions.LoadEntireSolution = true;
            SolutionWarmup.Reset();
            WorkspaceService.BindSolution(FixturePaths.MultiSolutionFile);

            await SolutionWarmup.Start();

            var hits = await SearchEverywhereHandler.SearchAsync(
                new SearchEverywhereParams("Caller"), default);

            Assert.Contains(hits.Items, item =>
                item.Name == "Caller"
                && item.Path.Contains("ProjectB", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            LspFeatureOptions.LoadEntireSolution = previousSetting;
            SolutionWarmup.Reset();
            WorkspaceService.BindSolution(previous);
        }
    }

    /// <summary>
    /// The caches a search reads are built before a search asks for them.
    /// </summary>
    /// <remarks>
    /// Asserted as "every project already holds its compilation" rather than as a stopwatch,
    /// because the thing being pinned is that the work happened, not how long it took. On a real
    /// solution the difference is a first query of seven seconds against one of a fifth of a
    /// second — and since the panel cancels its request on every keystroke, seconds per search is
    /// seconds per character, which a typist outruns until the search appears to find nothing.
    /// </remarks>
    [Fact]
    public async Task TheSymbolCachesAreBuiltBeforeAnyoneSearches()
    {
        string? previous = WorkspaceService.BoundSolutionPath;
        bool previousSetting = LspFeatureOptions.LoadEntireSolution;

        try
        {
            await WorkspaceService.EvictAllAsync();
            LspFeatureOptions.LoadEntireSolution = true;
            SolutionWarmup.Reset();
            WorkspaceService.BindSolution(FixturePaths.MultiSolutionFile);

            await SolutionWarmup.Start();
            await SolutionWarmup.WarmedSymbols;

            var solution = WorkspaceService.TryGetSessionSolution();
            Assert.NotNull(solution);
            Assert.NotEmpty(solution!.Projects);

            // TryGetCompilation, not GetCompilationAsync: the second would build the thing it is
            // supposed to be checking for and pass however cold the workspace was.
            Assert.All(solution.Projects, project =>
                Assert.True(
                    project.TryGetCompilation(out _),
                    $"'{project.Name}' had no compilation after the warm pass"));
        }
        finally
        {
            LspFeatureOptions.LoadEntireSolution = previousSetting;
            SolutionWarmup.Reset();
            WorkspaceService.BindSolution(previous);
        }
    }

    /// <summary>
    /// The Solution Explorer draws the same fact the search box depends on: a project the
    /// workspace has not loaded answers nothing, and a row that looks identical to a loaded one
    /// makes that indistinguishable from a project with nothing in it.
    /// </summary>
    [Fact]
    public async Task ProjectsTheWorkspaceHasNotLoadedAreMarked()
    {
        string? previous = WorkspaceService.BoundSolutionPath;

        try
        {
            await WorkspaceService.EvictAllAsync();
            SolutionWarmup.Reset();
            WorkspaceService.BindSolution(FixturePaths.MultiSolutionFile);

            var projects = await SolutionTreeHandler.ChildrenAsync(
                new SolutionTreeParams(NodeId: $"solution:{FixturePaths.MultiSolutionFile}"), default);

            Assert.NotEmpty(projects);
            Assert.All(projects, node =>
            {
                Assert.Equal("not loaded", node.Description);
                Assert.True(node.Dimmed);

                // Still expandable and still runnable-or-not on its own merits: not loaded yet is
                // not the user's Unload Project, and must not take the row's actions away.
                Assert.True(node.HasChildren);
                Assert.NotEqual(SolutionNodeKind.UnloadedProject, node.ContextValue);
            });
        }
        finally
        {
            WorkspaceService.BindSolution(previous);
        }
    }

    /// <summary>
    /// Off means off: nothing is loaded, and the solution-wide requests are as narrow as they were
    /// before this existed. The setting is what a solution too large to hold at once is left with.
    /// </summary>
    [Fact]
    public async Task TurningItOffLoadsNothing()
    {
        string? previous = WorkspaceService.BoundSolutionPath;
        bool previousSetting = LspFeatureOptions.LoadEntireSolution;

        try
        {
            await WorkspaceService.EvictAllAsync();
            LspFeatureOptions.LoadEntireSolution = false;
            SolutionWarmup.Reset();
            WorkspaceService.BindSolution(FixturePaths.MultiSolutionFile);

            await SolutionWarmup.Start();

            // And the wait a search does must not block on a load that was never started.
            await SolutionWarmup.WaitAsync(default);

            Assert.Null(WorkspaceService.TryGetSessionSolution());
        }
        finally
        {
            LspFeatureOptions.LoadEntireSolution = previousSetting;
            SolutionWarmup.Reset();
            WorkspaceService.BindSolution(previous);
        }
    }

    /// <summary>
    /// The project the user has a file open in is warmed before the ones they do not, whatever
    /// order the solution file happens to list them in.
    /// </summary>
    /// <remarks>
    /// Which project is warm decides more than how soon a background pass finishes. Semantics for
    /// an open file are served from a frozen snapshot of its project, and freezing a project whose
    /// compilation was never built yields a near-empty one — so a project waiting its turn behind
    /// seventeen others does not answer slowly, it answers thinly. This is the assertion that the
    /// project being looked at is not one of the seventeen.
    /// </remarks>
    [Fact]
    public void TheProjectWithAnOpenFileIsWarmedFirst()
    {
        const string session = "warm-order";
        string opened = Path.Combine(Path.GetTempPath(), "WarmOrder", "Last", "Open.cs");

        try
        {
            OpenDocumentStore.Open(session, opened, SourceText.From("class Open { }"), version: 1);

            // "Last" is listed last, so solution order alone would warm it last.
            var solution = SolutionOf(("First", "Other.cs"), ("Middle", "Third.cs"), ("Last", opened));

            var order = SolutionWarmup.WarmOrder(solution);

            Assert.Equal(
                new[] { "Last", "First", "Middle" },
                order.Select(project => project.Name).ToArray());
        }
        finally
        {
            OpenDocumentStore.Close(session, opened);
        }
    }

    /// <summary>
    /// With nothing open there is nothing to prefer, and the pass is the solution's own order —
    /// no reshuffling of a list whose head is as good a guess as any other.
    /// </summary>
    [Fact]
    public void WithNothingOpenTheOrderIsTheSolutionsOwn()
    {
        var solution = SolutionOf(("First", "A.cs"), ("Middle", "B.cs"), ("Last", "C.cs"));

        Assert.Equal(
            new[] { "First", "Middle", "Last" },
            SolutionWarmup.WarmOrder(solution).Select(project => project.Name).ToArray());
    }

    /// <summary>
    /// The sweep reaches every document of every project — the per-document indexes that
    /// find-references narrows through are not built by the declaration pass that follows it.
    /// </summary>
    [Fact]
    public async Task TheSweepReachesEveryDocument()
    {
        var solution = SolutionOf(
            ("Alpha", new[] { "Alpha1.cs", "Alpha2.cs" }),
            ("Beta", new[] { "Beta1.cs" }));

        int swept = 0;

        await SolutionWarmup.SweepIndexesAsync(
            solution, solution.Projects.ToList(), default, () => Interlocked.Increment(ref swept));

        Assert.Equal(3, swept);
    }

    /// <summary>
    /// Cancelling the index sweep stops it, rather than letting a shutdown wait on thousands of
    /// documents that nobody will ask about.
    /// </summary>
    /// <remarks>
    /// Asserted as "the count stopped moving", not as a stopwatch reading: what a throttled
    /// background pass has to promise is that it gives the work up, and an elapsed time cannot tell
    /// a sweep that stopped from a sweep that was merely fast. The document count is large enough
    /// that finishing it inside the cancellation delay is not a plausible alternative explanation.
    /// </remarks>
    [Fact]
    public async Task CancellingTheIndexSweepStopsIt()
    {
        var solution = SolutionOf(("Wide", Enumerable.Range(0, 1500)
            .Select(i => Path.Combine(Path.GetTempPath(), "WarmSweep", $"Type{i}.cs"))
            .ToArray()));

        int swept = 0;

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(25));

        var started = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SolutionWarmup.SweepIndexesAsync(
                solution, solution.Projects.ToList(), cts.Token,
                () => Interlocked.Increment(ref swept)));
        started.Stop();

        int atCancellation = Volatile.Read(ref swept);

        Assert.True(
            atCancellation < 1500,
            $"the sweep indexed all 1500 documents despite cancelling after {started.ElapsedMilliseconds}ms");

        // Not merely fewer than all of them: still fewer a moment later, which is the difference
        // between a sweep that stopped and one that was cancelled and carried on regardless.
        await Task.Delay(300);
        Assert.Equal(atCancellation, Volatile.Read(ref swept));
    }

    /// <summary>
    /// A solution of C# projects with the named documents, built in memory: the ordering and the
    /// sweep are both about which documents exist and where, which needs no MSBuild evaluation.
    /// </summary>
    private static Solution SolutionOf(params (string Project, string[] Documents)[] projects)
    {
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;

        foreach (var (name, documents) in projects)
        {
            var id = ProjectId.CreateNewId(name);

            solution = solution.AddProject(ProjectInfo.Create(
                id, VersionStamp.Default, name, name, LanguageNames.CSharp,
                documents: documents.Select(path => DocumentInfo.Create(
                    DocumentId.CreateNewId(id),
                    Path.GetFileName(path),
                    loader: new StaticTextLoader(Path.GetFileNameWithoutExtension(path)),
                    filePath: path)).ToImmutableArray()));
        }

        return solution;
    }

    private static Solution SolutionOf(params (string Project, string Document)[] projects) =>
        SolutionOf(projects.Select(p => (p.Project, new[] { p.Document })).ToArray());

    /// <summary>A document body distinct per file, so no two share a checksum and an index.</summary>
    private sealed class StaticTextLoader(string typeName) : TextLoader
    {
        public override Task<TextAndVersion> LoadTextAndVersionAsync(
            LoadTextOptions options, CancellationToken cancellationToken) =>
            Task.FromResult(TextAndVersion.Create(
                SourceText.From($"namespace Warm; public class {typeName} {{ public int Value; }}"),
                VersionStamp.Default));
    }
}

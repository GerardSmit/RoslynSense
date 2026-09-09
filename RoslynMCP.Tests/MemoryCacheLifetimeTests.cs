using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Services;
using RoslynMCP.Services.Memory;
using Xunit;

namespace RoslynMCP.Tests;

internal sealed class MemoryTestClock : TimeProvider
{
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _ticks;
    public void Advance(TimeSpan duration) => _ticks += duration.Ticks;
}

[Collection(SharedState.Name)]
public class MemoryCacheLifetimeTests
{
    [Fact]
    public void CompletionFinishingAfterInvalidationCannotRestoreOldSnapshot()
    {
        using var workspace = new AdhocWorkspace();
        var document = Document(workspace);
        using var cache = new LspResolveCache();
        var generation = cache.Generation;
        cache.DocumentChanged(document.FilePath!);
        Assert.Equal(-1, cache.StoreCompletions(document, [], generation));
        Assert.Equal(0, cache.Inspect().StrongEntries);
    }

    [Fact]
    public void WeakPayloadAndItsBackReferencesAreCollectible()
    {
        var cache = new WeakSnapshotCache<int, object>("test.weak", 0);
        var weak = PutWeak(cache);
        Collect();
        Assert.False(weak.IsAlive);
        cache.Trim();
        Assert.Equal(0, cache.Inspect().Entries);
        GC.KeepAlive(cache);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference PutWeak(WeakSnapshotCache<int, object> cache)
    {
        var payload = new object[1024];
        payload[0] = payload;
        cache.Set(1, payload, cache.Generation);
        return new WeakReference(payload);
    }

    [Fact]
    public void StrongBudgetAndIdleExpiryApplyWithoutAnotherLookup()
    {
        var clock = new MemoryTestClock();
        var cache = new WeakSnapshotCache<int, object>("test.recent", 2, time: clock);
        var values = Enumerable.Range(0, 8).Select(_ => new object()).ToArray();
        for (int i = 0; i < values.Length; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            cache.Set(i, values[i], cache.Generation, retain: true);
        }
        Assert.Equal(2, cache.Inspect().StrongEntries);
        clock.Advance(TimeSpan.FromMinutes(2));
        MemoryCacheRegistry.Trim();
        Assert.Equal(0, cache.Inspect().StrongEntries);
        GC.KeepAlive(values);
    }

    [Fact]
    public void InvalidationRejectsAnOlderBuildAndBoundsMetadata()
    {
        var cache = new WeakSnapshotCache<int, object>("test.generation", 1, entryLimit: 4);
        var generation = cache.Generation;
        cache.Clear();
        cache.Set(1, new object(), generation, true);
        Assert.False(cache.TryGet(1, out _));
        var values = Enumerable.Range(0, 10).Select(_ => new object()).ToArray();
        for (int i = 0; i < values.Length; i++) cache.Set(i, values[i], cache.Generation);
        Assert.InRange(cache.Inspect().Entries, 0, 4);
        GC.KeepAlive(values);
    }

    [Fact]
    public void SlowMenuConstructionGetsItsFullLifetimeAfterItIsOffered()
    {
        using var workspace = new AdhocWorkspace();
        var document = Document(workspace);
        var clock = new MemoryTestClock();
        using var cache = new LspResolveCache(clock);
        long id;
        using (var group = cache.BeginActions(document))
        {
            id = cache.StoreAction(Action(document), document.Project.Solution, group.Id);
            clock.Advance(TimeSpan.FromMinutes(3));
            cache.Trim();
            Assert.NotNull(cache.GetAction(id));
        }
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.NotNull(cache.GetAction(id));
        clock.Advance(TimeSpan.FromMinutes(2));
        cache.Trim();
        Assert.Null(cache.GetAction(id));
    }

    [Fact]
    public void CompleteActionMenuSurvivesItsOwnLargeNumberOfLeaves()
    {
        using var workspace = new AdhocWorkspace();
        var document = Document(workspace);
        using var cache = new LspResolveCache();
        long first;
        using (var request = cache.BeginActions(document))
        {
            first = cache.StoreAction(Action(document), document.Project.Solution, request.Id);
            for (int i = 0; i < 400; i++) cache.StoreAction(Action(document), document.Project.Solution, request.Id);
        }
        Assert.NotNull(cache.GetAction(first));
    }

    [Fact]
    public void ActionExpiryAndBufferChangesReleaseEntireGroups()
    {
        using var workspace = new AdhocWorkspace();
        var document = Document(workspace);
        var clock = new MemoryTestClock();
        using var cache = new LspResolveCache(clock);
        long id;
        using (var group = cache.BeginActions(document)) id = cache.StoreAction(Action(document), document.Project.Solution, group.Id);
        clock.Advance(TimeSpan.FromMinutes(3));
        MemoryCacheRegistry.Trim();
        Assert.Null(cache.GetAction(id));
        Assert.Equal(0, cache.Inspect().StrongEntries);
        using (var group = cache.BeginActions(document))
        {
            MemoryCacheRegistry.DocumentChanged("another-file.cs");
            id = cache.StoreAction(Action(document), document.Project.Solution, group.Id);
        }
        Assert.Null(cache.GetAction(id));
    }

    [Fact]
    public void OnlyTwoCompletedMenusAreRetainedAndDisposedCachesCannotRepopulate()
    {
        using var workspace = new AdhocWorkspace();
        var document = Document(workspace);
        using var cache = new LspResolveCache();
        long first = 0;
        for (int i = 0; i < 3; i++)
        {
            using var group = cache.BeginActions(document);
            long id = cache.StoreAction(Action(document), document.Project.Solution, group.Id);
            if (i == 0) first = id;
        }
        Assert.Null(cache.GetAction(first));
        Assert.Equal(2, cache.Inspect().StrongEntries);
        cache.Dispose();
        first = cache.StoreAction(Action(document), document.Project.Solution);
        Assert.Null(cache.GetAction(first));
    }

    [Fact]
    public void CompletionExpiryReleasesDocumentWithoutAReplacementRequest()
    {
        using var workspace = new AdhocWorkspace();
        var clock = new MemoryTestClock();
        using var cache = new LspResolveCache(clock);
        long id = cache.StoreCompletions(Document(workspace), [Microsoft.CodeAnalysis.Completion.CompletionItem.Create("Test")]);
        Assert.NotNull(cache.GetCompletion(id, 0));
        clock.Advance(TimeSpan.FromMinutes(3));
        MemoryCacheRegistry.Trim();
        Assert.Null(cache.GetCompletion(id, 0));
        Assert.Equal(0, cache.Inspect().StrongEntries);
    }

    [Fact]
    public void MaintenanceDoesNotOwnDisconnectedResolveCaches()
    {
        var weak = CreateResolveCache();
        Collect();
        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateResolveCache() => new(new LspResolveCache());

    [Fact]
    public async Task BoundMarkupAndSingleDocumentProjectionAreCollectibleTogether()
    {
        var weak = await BindAndProject();
        AspxDocumentService.ReleaseStrong();
        AspxProjectionService.ReleaseStrong();
        Collect();
        Assert.False(weak.IsAlive);
        var rebound = await AspxDocumentService.GetAsync(FixturePaths.DefaultAspxFile, default);
        Assert.NotNull(rebound?.Tree);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> BindAndProject()
    {
        var document = await AspxDocumentService.GetAsync(FixturePaths.DefaultAspxFile, default);
        Assert.NotNull(document);
        _ = AspxProjectionService.Get(document);
        return new WeakReference(document);
    }

    [Fact]
    public async Task WholeProjectProjectionIsCollectibleAfterRecentLeaseEnds()
    {
        var weak = await BuildProjectProjection();
        AspxDocumentService.ReleaseStrong();
        AspxProjectionService.ReleaseStrong();
        Collect();
        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> BuildProjectProjection()
    {
        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(FixturePaths.AspxProjectFile);
        var projection = await AspxProjectionService.GetProjectAsync(project, default);
        Assert.NotNull(projection);
        return new WeakReference(projection);
    }

    [Fact]
    public async Task ConcurrentMarkupReadsShareOneBindingAndCancelledReadDoesNotPoisonIt()
    {
        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(FixturePaths.AspxProjectFile);
        AspxDocumentService.Invalidate(FixturePaths.DefaultAspxFile);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AspxDocumentService.GetAsync(FixturePaths.DefaultAspxFile, project, cancelled.Token));
        var documents = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            AspxDocumentService.GetAsync(FixturePaths.DefaultAspxFile, project, default, retain: true)));
        Assert.NotNull(documents[0]);
        Assert.All(documents, document => Assert.Same(documents[0], document));
    }

    [Fact]
    public async Task SameMarkupInDifferentProjectContextsDoesNotShareBoundSymbols()
    {
        var original = await AspxDocumentService.GetAsync(FixturePaths.DefaultAspxFile, default);
        Assert.NotNull(original);
        var project = original.Project;
        using var workspace = new AdhocWorkspace();
        var other = workspace.AddProject("Other", LanguageNames.CSharp)
            .WithMetadataReferences(project.MetadataReferences);
        var bound = await AspxDocumentService.GetAsync(FixturePaths.DefaultAspxFile, other, default);
        Assert.NotNull(bound);
        Assert.NotSame(original, bound);
        Assert.Equal(other.Id, bound.Project.Id);
    }

    private static Document Document(AdhocWorkspace workspace)
    {
        var project = workspace.AddProject("Test", LanguageNames.CSharp);
        return workspace.AddDocument(project.Id, "Test.cs", SourceText.From("class C {}"));
    }
    private static CodeAction Action(Document document) => CodeAction.Create("Test", _ => Task.FromResult(document));
    internal static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
}

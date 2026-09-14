using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Config;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Search;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

[Collection(SharedState.Name)]
public sealed class IdleWarmupTests
{
    [Fact]
    public async Task IdleAdmissionWaitsForForegroundAndSupportsCancellation()
    {
        using var busy = ForegroundGate.Busy();
        using var cancellation = new CancellationTokenSource();
        var waiting = ForegroundGate.WaitForIdleAsync(TimeSpan.Zero, cancellation.Token);
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        busy.Dispose();
        await ForegroundGate.WaitForIdleAsync(TimeSpan.Zero, default).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ExplicitSearchCanPromoteAnIdleLoadWithoutWaitingForItsOwnBusyScope()
    {
        bool previous = LspFeatureOptions.LoadEntireSolution;
        using var binding = WorkspaceService.BindSolutionForTesting(FixturePaths.MultiSolutionFile);
        using var busy = ForegroundGate.Busy();
        try
        {
            LspFeatureOptions.LoadEntireSolution = true;
            SolutionWarmup.Reset();
            var warm = SolutionWarmup.Start();
            Assert.False(warm.IsCompleted);
            await SolutionWarmup.WaitAsync(default).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(warm.IsCompleted);
        }
        finally
        {
            SolutionWarmup.Reset();
            LspFeatureOptions.LoadEntireSolution = previous;
        }
    }

    [Fact]
    public async Task IdleLoadsSolutionAndRewarmsEditedSnapshotWithoutReplacingUnchangedProjectCompilation()
    {
        bool previous = LspFeatureOptions.LoadEntireSolution;
        string file = FixturePaths.MultiProjectBClassFile;
        string session = Guid.NewGuid().ToString("N");
        using var binding = WorkspaceService.BindSolutionForTesting(FixturePaths.MultiSolutionFile);
        try
        {
            await WorkspaceService.EvictAllAsync();
            SolutionWarmup.Reset();
            LspFeatureOptions.LoadEntireSolution = true;
            ForegroundGate.Touch();
            await SolutionWarmup.Start().WaitAsync(TimeSpan.FromSeconds(30));
            await SolutionWarmup.WarmedSymbols.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(SolutionWarmup.IsPrepared);
            var before = WorkspaceService.TryGetSessionSolution()!;
            var unchanged = before.Projects.Single(p => p.FilePath == FixturePaths.MultiProjectAFile);
            Assert.True(unchanged.TryGetCompilation(out var compilation));

            OpenDocumentStore.Open(session, file, SourceText.From(await File.ReadAllTextAsync(file) + "\n// unsaved"), 1);
            await WorkspaceService.ReconcileOpenBufferAsync(file);
            Assert.False(SolutionWarmup.IsPrepared);
            SolutionWarmup.NotifyActivity();
            await SolutionWarmup.WarmedSymbols.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(SolutionWarmup.IsPrepared);
            var after = WorkspaceService.TryGetSessionSolution()!;
            Assert.True(after.GetProject(unchanged.Id)!.TryGetCompilation(out var reused));
            Assert.Same(compilation, reused);
            var edited = after.GetDocument(after.GetDocumentIdsWithFilePath(file).Single())!;
            Assert.EndsWith("// unsaved", (await edited.GetTextAsync()).ToString());
        }
        finally
        {
            OpenDocumentStore.Close(session, file);
            await WorkspaceService.ReconcileOpenBufferAsync(file);
            SolutionWarmup.Reset();
            LspFeatureOptions.LoadEntireSolution = previous;
        }
    }
}

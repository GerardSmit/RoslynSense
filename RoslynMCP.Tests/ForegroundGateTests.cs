using RoslynMCP.Lsp.Search;
using Xunit;

namespace RoslynMCP.Tests;

[Collection(SharedState.Name)]
public sealed class ForegroundGateTests
{
    [Fact]
    public async Task NestedCompletionScopesResumeOnlyAfterTheLastDistinctScopeEnds()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var outer = ForegroundGate.PauseBackground();
        using var inner = ForegroundGate.PauseBackground();
        var admitted = ForegroundGate.AdmitAsync(timeout.Token).AsTask();
        try
        {
            Assert.False(admitted.IsCompleted);
            outer.Dispose();
            outer.Dispose();
            Assert.False(admitted.IsCompleted);
            inner.Dispose();
            using var slot = await admitted;
            Assert.Null(slot);
        }
        finally
        {
            outer.Dispose();
            inner.Dispose();
            timeout.Cancel();
            await DrainAsync([admitted]);
        }
    }

    [Fact]
    public async Task EndingTheLastCompletionResumesEveryWaitingIndexItem()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var pause = ForegroundGate.PauseBackground();
        var pending = Enumerable.Range(0, 8)
            .Select(_ => ForegroundGate.AdmitAsync(timeout.Token).AsTask()).ToArray();
        try
        {
            Assert.All(pending, task => Assert.False(task.IsCompleted));
            pause.Dispose();
            Assert.All(await Task.WhenAll(pending), slot => Assert.Null(slot));
        }
        finally
        {
            pause.Dispose();
            timeout.Cancel();
            await DrainAsync(pending);
        }
    }

    [Fact]
    public async Task CancellingOnePausedItemDoesNotResumeOrCancelOtherItems()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var canceled = new CancellationTokenSource();
        using var pause = ForegroundGate.PauseBackground();
        var abandoned = ForegroundGate.AdmitAsync(canceled.Token).AsTask();
        var remaining = ForegroundGate.AdmitAsync(timeout.Token).AsTask();
        try
        {
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
            Assert.False(remaining.IsCompleted);
            pause.Dispose();
            using var slot = await remaining;
            Assert.Null(slot);

            // A later completion needs a new resume signal, even though the previous one fired.
            using var nextPause = ForegroundGate.PauseBackground();
            var next = ForegroundGate.AdmitAsync(timeout.Token).AsTask();
            try
            {
                Assert.False(next.IsCompleted);
                nextPause.Dispose();
                using var nextSlot = await next;
            }
            finally
            {
                nextPause.Dispose();
                timeout.Cancel();
                await DrainAsync([next]);
            }
        }
        finally
        {
            pause.Dispose();
            canceled.Cancel();
            timeout.Cancel();
            await DrainAsync([abandoned, remaining]);
        }
    }

    [Fact]
    public async Task SearchSlotsRemainAvailableAfterACancelledPausedWaiter()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var canceled = new CancellationTokenSource();
        using var busy = ForegroundGate.Busy();
        var slots = new List<IDisposable>();
        Task<IDisposable?>? pending = null;
        try
        {
            for (int index = 0; index < 3; index++)
                slots.Add((await ForegroundGate.AdmitAsync(timeout.Token))!);
            pending = ForegroundGate.AdmitAsync(canceled.Token).AsTask();
            Assert.False(pending.IsCompleted);
            using (ForegroundGate.PauseBackground())
            {
                slots[0].Dispose();
                slots.RemoveAt(0);
                canceled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            }
            foreach (var slot in slots)
                slot.Dispose();
            slots.Clear();

            // Whether cancellation happened in the slot queue or in the subsequent pause wait,
            // it must return every reserved slot before a later search begins.
            for (int index = 0; index < 3; index++)
                slots.Add((await ForegroundGate.AdmitAsync(timeout.Token))!);
        }
        finally
        {
            canceled.Cancel();
            timeout.Cancel();
            foreach (var slot in slots)
                slot.Dispose();
            if (pending is not null)
                await DrainAsync([pending]);
        }
    }

    private static async Task DrainAsync(IEnumerable<Task<IDisposable?>> tasks)
    {
        foreach (var task in tasks)
        {
            try { (await task)?.Dispose(); }
            catch (OperationCanceledException) { }
        }
    }
}

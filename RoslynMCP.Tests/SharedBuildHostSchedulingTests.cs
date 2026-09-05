using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

public sealed class SharedBuildHostSchedulingTests
{
    [Fact]
    public async Task SynchronousWorkInDifferentNonemptyShardsRunsConcurrently()
    {
        using var entered = new CountdownEvent(2);
        int calls = 0;
        var result = await SharedBuildHost.RunShardBatchAsync(
            [(0, new List<string>()), (1, new List<string> { "First", "Second" }), (2, new List<string> { "Third" })],
            (index, paths) =>
            {
                Interlocked.Increment(ref calls);
                Assert.NotEmpty(paths);
                entered.Signal();
                // Model a cached load's synchronous file IO before it returns a completed task.
                // With direct async-method enumeration, the first shard would wait here while
                // the second had not even been called. One work item per project would also
                // violate the two-shard bound despite there being three input projects.
                Assert.True(entered.Wait(TimeSpan.FromSeconds(15)), "A shard blocked the next shard from starting.");
                return Task.FromResult(index);
            }, default);

        Assert.Equal(2, calls);
        Assert.Equal<int>([1, 2], result);
    }

    [Fact]
    public async Task ACancelledBatchDoesNotStartShardWork()
    {
        int calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SharedBuildHost.RunShardBatchAsync(
            [(0, new List<string> { "First" }), (1, new List<string> { "Second" })],
            (_, _) => Task.FromResult(Interlocked.Increment(ref calls)),
            new CancellationToken(canceled: true)));
        Assert.Equal(0, calls);
    }
}

using Microsoft.CodeAnalysis;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

public sealed class SearchScopeConcurrencyTests
{
    [Fact]
    public async Task ConcurrentNavigationRequestsStartOneConsumerLoad()
    {
        using var workspace = new AdhocWorkspace();
        string path = "search-scope-" + Guid.NewGuid().ToString("N");
        using var ready = new Barrier(8);
        using var releaseFactory = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int starts = 0;

        Task Load()
        {
            Interlocked.Increment(ref starts);
            entered.TrySetResult();
            if (!releaseFactory.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Consumer load factory was not released.");
            return finished.Task;
        }

        var callers = Enumerable.Range(0, 8).Select(_ => Task.Factory.StartNew(() =>
        {
            if (!ready.SignalAndWait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Navigation requests did not rendezvous.");
            return SearchScopeService.ConsumerLoadFor(path, workspace, Load);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(100); // Give every dedicated caller time to enter the blocked factory.
            releaseFactory.Set();
            var loads = await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, starts);
            Assert.All(loads, load => Assert.Same(finished.Task, load));
        }
        finally
        {
            releaseFactory.Set();
            finished.TrySetResult();
            await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(10));
            SearchScopeService.ForgetConsumerLoad(path, workspace);
        }
    }

    [Fact]
    public async Task OldWorkspaceFailureCannotForgetTheReplacementLoad()
    {
        using var original = new AdhocWorkspace();
        using var replacement = new AdhocWorkspace();
        string path = "search-scope-" + Guid.NewGuid().ToString("N");
        var newer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var old = SearchScopeService.ConsumerLoadFor(path, original,
                () => Task.FromException(new IOException("old workspace load failed")));
            var current = SearchScopeService.ConsumerLoadFor(path, replacement, () => newer.Task);
            await Assert.ThrowsAsync<IOException>(() => old);
            SearchScopeService.ForgetConsumerLoad(path, original, old);
            Assert.Same(current, SearchScopeService.ConsumerLoadFor(path, replacement,
                () => throw new InvalidOperationException("A replacement load was started twice.")));
        }
        finally
        {
            newer.TrySetResult();
            SearchScopeService.ForgetConsumerLoad(path, replacement);
        }
    }

    [Fact]
    public async Task LateFailureWaiterCannotForgetARetryInTheSameWorkspace()
    {
        using var workspace = new AdhocWorkspace();
        string path = "search-scope-" + Guid.NewGuid().ToString("N");
        var retry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var failed = SearchScopeService.ConsumerLoadFor(path, workspace,
                () => Task.FromException(new IOException("initial load failed")));
            await Assert.ThrowsAsync<IOException>(() => failed);
            SearchScopeService.ForgetConsumerLoad(path, workspace, failed);
            var replacement = SearchScopeService.ConsumerLoadFor(path, workspace, () => retry.Task);

            // The second waiter on the old failure reaches its catch after a retry started.
            SearchScopeService.ForgetConsumerLoad(path, workspace, failed);
            Assert.Same(replacement, SearchScopeService.ConsumerLoadFor(path, workspace,
                () => throw new InvalidOperationException("The pending retry was evicted.")));
        }
        finally
        {
            retry.TrySetResult();
            SearchScopeService.ForgetConsumerLoad(path, workspace);
        }
    }

    [Fact]
    public async Task FailedLoadCanRetryAfterItIsForgotten()
    {
        using var workspace = new AdhocWorkspace();
        string path = "search-scope-" + Guid.NewGuid().ToString("N");
        int starts = 0;
        Task Load() => Interlocked.Increment(ref starts) == 1
            ? Task.FromException(new IOException("transient load failure")) : Task.CompletedTask;
        try
        {
            await Assert.ThrowsAsync<IOException>(() => SearchScopeService.ConsumerLoadFor(path, workspace, Load));
            SearchScopeService.ForgetConsumerLoad(path, workspace);
            await SearchScopeService.ConsumerLoadFor(path, workspace, Load);
            Assert.Equal(2, starts);
        }
        finally { SearchScopeService.ForgetConsumerLoad(path, workspace); }
    }
}

using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The gate that keeps one restore per solution in flight — and, in particular, what happens to it
/// when the caller waiting on that restore is the one that goes away.
/// </summary>
public class SingleFlightTests
{
    [Fact]
    public async Task ConcurrentCallersOnlyInvokeTheFactoryOnce()
    {
        var flight = new SingleFlight();
        using var callersReady = new Barrier(8);
        using var releaseFactory = new ManualResetEventSlim();
        var factoryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;

        Task Run(string _)
        {
            Interlocked.Increment(ref started);
            factoryEntered.TrySetResult();
            if (!releaseFactory.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The test did not release the factory.");
            return runFinished.Task;
        }

        // Dedicated threads keep a blocked factory from starving the other contenders.
        var callers = Enumerable.Range(0, 8).Select(_ => Task.Factory.StartNew(() =>
        {
            if (!callersReady.SignalAndWait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The callers did not rendezvous.");
            return flight.Start("target", Run);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        try
        {
            await factoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // Keep the factory open while the other threads contend for the same entry.
            await Task.Delay(100);
        }
        finally
        {
            releaseFactory.Set();
        }

        var runs = await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(10));
        runFinished.SetResult();
        await Task.WhenAll(runs);
        Assert.Equal(1, started);
        Assert.All(runs, run => Assert.Same(runs[0], run));
    }

    [Fact]
    public async Task ASynchronousFactoryFailureDoesNotPoisonTheKey()
    {
        var flight = new SingleFlight();
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = flight.Start("target", _ =>
                throw new InvalidOperationException("failed before returning a task"));
        });
        Assert.False(flight.IsInFlight("target"));

        await flight.Start("target", _ => Task.CompletedTask);
        Assert.False(flight.IsInFlight("target"));
    }

    [Fact]
    public async Task SecondCallerJoinsTheRunTheFirstStarted()
    {
        var flight = new SingleFlight();
        var gate = new TaskCompletionSource();
        int started = 0;

        Task Run(string _)
        {
            Interlocked.Increment(ref started);
            return gate.Task;
        }

        var first = flight.Start("target", Run);
        var second = flight.Start("target", Run);

        Assert.Same(first, second);
        Assert.Equal(1, started);

        gate.SetResult();
        await first;
    }

    [Fact]
    public async Task ACallerGivingUpDoesNotLetTheNextOneStartASecondRun()
    {
        // The bug this exists for. A waiter's token firing is the ordinary case in an editor, and
        // evicting the entry there dropped it while the work behind it was still running — so the
        // next caller started a second MSBuild restore on top of the first, both writing one
        // project.assets.json. NuGet reports that as "Cannot create a file when that file already
        // exists", on a solution that restores fine from a shell.
        var flight = new SingleFlight();
        var gate = new TaskCompletionSource();
        int started = 0;

        Task Run(string _)
        {
            Interlocked.Increment(ref started);
            return gate.Task;
        }

        var run = flight.Start("target", Run);

        using var cts = new CancellationTokenSource();
        var waiter = run.WaitAsync(cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAsync<TaskCanceledException>(() => waiter);

        // The run itself has not finished, so it is still the run for this target.
        Assert.True(flight.IsInFlight("target"));
        Assert.Same(run, flight.Start("target", Run));
        Assert.Equal(1, started);

        gate.SetResult();
        await run;
    }

    [Fact]
    public async Task AFinishedRunIsNotJoinedByTheNextCaller()
    {
        // The other half, and the reason the entry is not simply left in place: a restore that
        // failed on a transient blip must not answer for the target for the life of the process.
        var flight = new SingleFlight();
        int started = 0;

        Task Run(string _)
        {
            Interlocked.Increment(ref started);
            return Task.FromException(new InvalidOperationException("feed unreachable"));
        }

        var first = flight.Start("target", Run);
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);

        Assert.False(flight.IsInFlight("target"));

        var second = flight.Start("target", Run);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second);

        Assert.NotSame(first, second);
        Assert.Equal(2, started);
    }
}

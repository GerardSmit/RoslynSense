using RoslynMCP.Lsp;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// When a batch of background diagnostics passes asks the editor to re-pull.
/// </summary>
/// <remarks>
/// One refresh per changed document was the amplifier behind "slow after a rebuild": a rebuild
/// queued a pass for every closed file, every completion asked for a refresh, and every refresh
/// ran the sweep that queued the next round. The batch answers once at the start, at most once
/// per interval while it runs, and once more when it settles.
/// </remarks>
public class RefreshBatchTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    [Fact]
    public void ASinglePassThatChangedSomethingRefreshes()
    {
        var batch = new RefreshBatch(Interval, () => 0);

        batch.Enter();

        Assert.True(batch.Leave(changed: true));
    }

    [Fact]
    public void APassThatChangedNothingDoesNot()
    {
        var batch = new RefreshBatch(Interval, () => 0);

        batch.Enter();

        Assert.False(batch.Leave(changed: false));
    }

    [Fact]
    public void ABatchRefreshesAtItsFirstChangeAndAgainWhenItSettles()
    {
        long now = 0;
        var batch = new RefreshBatch(Interval, () => now);

        batch.Enter();
        batch.Enter();
        batch.Enter();

        // The first result lands quickly — squiggles rather than a "Loading..." hover.
        Assert.True(batch.Leave(changed: true));

        // The second change within the interval waits for the batch.
        now += 1_000;
        Assert.False(batch.Leave(changed: true));

        // The last pass changed nothing itself, but the batch settles with a change unannounced.
        now += 1_000;
        Assert.True(batch.Leave(changed: false));
        Assert.Equal(0, batch.Pending);
    }

    [Fact]
    public void ALongBatchRefreshesOncePerInterval()
    {
        long now = 0;
        var batch = new RefreshBatch(Interval, () => now);

        for (int i = 0; i < 4; i++)
            batch.Enter();

        Assert.True(batch.Leave(changed: true));

        now += 5_000;
        Assert.False(batch.Leave(changed: true));

        // Past the interval with a change pending: an interim refresh, so a batch that takes
        // minutes still shows progress.
        now += Interval.Milliseconds + 15_000;
        Assert.True(batch.Leave(changed: true));

        // Nothing changed since that refresh, so settling announces nothing.
        now += 1_000;
        Assert.False(batch.Leave(changed: false));
    }

    [Fact]
    public void ASettledBatchWithNoChangesStaysQuiet()
    {
        var batch = new RefreshBatch(Interval, () => 0);

        batch.Enter();
        batch.Enter();

        Assert.False(batch.Leave(changed: false));
        Assert.False(batch.Leave(changed: false));
    }

    [Fact]
    public void ANewBatchAfterASettledOneRefreshesOnItsOwn()
    {
        long now = 0;
        var batch = new RefreshBatch(Interval, () => now);

        batch.Enter();
        Assert.True(batch.Leave(changed: true));

        // Well within the interval, but a batch of its own: the refresh it asks for is the one
        // that shows its result, and the interval only spaces refreshes within one batch.
        now += 1_000;
        batch.Enter();
        Assert.True(batch.Leave(changed: true));
    }
}

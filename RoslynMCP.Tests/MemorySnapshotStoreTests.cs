using RoslynMCP.Services.Memory;
using Xunit;

namespace RoslynMCP.Tests;

public class MemorySnapshotStoreTests
{
    private static MemorySnapshotStore.HeapSnapshot CreateSnapshot(
        string id, params (string Type, long Count, long Bytes)[] types)
    {
        var byType = types.ToDictionary(
            t => t.Type,
            t => new MemorySnapshotStore.TypeStat(t.Type, t.Count, t.Bytes));

        return new MemorySnapshotStore.HeapSnapshot(
            id, $"test {id}", DateTime.UtcNow, Pid: 1234,
            TotalHeapBytes: types.Sum(t => t.Bytes),
            ObjectCount: types.Sum(t => t.Count),
            Gen0Bytes: 0, Gen1Bytes: 0, Gen2Bytes: 0,
            LargeObjectBytes: 0, PinnedObjectBytes: 0,
            byType);
    }

    [Fact]
    public void WhenTypeGrowsThenDiffReportsPositiveDeltas()
    {
        var baseline = CreateSnapshot("a", ("App.Order", 100, 10_000));
        var target = CreateSnapshot("b", ("App.Order", 250, 25_000));

        var deltas = MemorySnapshotStore.Diff(baseline, target, maxResults: 10);

        var order = Assert.Single(deltas);
        Assert.Equal("App.Order", order.TypeName);
        Assert.Equal(150, order.CountDelta);
        Assert.Equal(15_000, order.BytesDelta);
    }

    [Fact]
    public void WhenTypeOnlyExistsInTargetThenBaselineCountsAsZero()
    {
        var baseline = CreateSnapshot("a");
        var target = CreateSnapshot("b", ("App.LeakedHandler", 50, 5_000));

        var deltas = MemorySnapshotStore.Diff(baseline, target, maxResults: 10);

        var leaked = Assert.Single(deltas);
        Assert.Equal(0, leaked.BaseCount);
        Assert.Equal(50, leaked.CountDelta);
        Assert.Equal(5_000, leaked.BytesDelta);
    }

    [Fact]
    public void WhenTypeIsCollectedThenDiffReportsNegativeDeltas()
    {
        var baseline = CreateSnapshot("a", ("App.Temp", 40, 4_000));
        var target = CreateSnapshot("b");

        var deltas = MemorySnapshotStore.Diff(baseline, target, maxResults: 10);

        var collected = Assert.Single(deltas);
        Assert.Equal(-40, collected.CountDelta);
        Assert.Equal(-4_000, collected.BytesDelta);
    }

    [Fact]
    public void WhenTypeIsUnchangedThenItIsOmittedFromDiff()
    {
        var baseline = CreateSnapshot("a", ("System.String", 10, 1_000), ("App.Order", 1, 100));
        var target = CreateSnapshot("b", ("System.String", 10, 1_000), ("App.Order", 2, 200));

        var deltas = MemorySnapshotStore.Diff(baseline, target, maxResults: 10);

        var order = Assert.Single(deltas);
        Assert.Equal("App.Order", order.TypeName);
    }

    [Fact]
    public void WhenMultipleTypesChangeThenDiffOrdersByBytesGrownDescending()
    {
        var baseline = CreateSnapshot("a",
            ("App.Small", 1, 100),
            ("App.Big", 1, 100),
            ("App.Shrinking", 10, 10_000));
        var target = CreateSnapshot("b",
            ("App.Small", 2, 600),
            ("App.Big", 5, 50_100),
            ("App.Shrinking", 1, 1_000));

        var deltas = MemorySnapshotStore.Diff(baseline, target, maxResults: 10);

        Assert.Equal(["App.Big", "App.Small", "App.Shrinking"], deltas.Select(d => d.TypeName));
    }

    [Fact]
    public void WhenMaxResultsIsSmallerThanChangesThenDiffTruncates()
    {
        var baseline = CreateSnapshot("a");
        var target = CreateSnapshot("b",
            ("App.A", 1, 300), ("App.B", 1, 200), ("App.C", 1, 100));

        var deltas = MemorySnapshotStore.Diff(baseline, target, maxResults: 2);

        Assert.Equal(2, deltas.Count);
        Assert.Equal("App.A", deltas[0].TypeName);
    }

    [Fact]
    public void WhenSnapshotIsStoredThenItCanBeRetrievedAndListed()
    {
        var store = new MemorySnapshotStore();
        var snapshot = CreateSnapshot(MemorySnapshotStore.NewId(), ("App.Order", 1, 100));

        var id = store.Store(snapshot);

        Assert.Same(snapshot, store.Get(id));
        Assert.Contains(store.List(), s => s.Id == id);
    }

    [Fact]
    public void WhenSnapshotIdIsUnknownThenGetReturnsNull()
    {
        var store = new MemorySnapshotStore();

        Assert.Null(store.Get("heap-000000-none"));
    }
}

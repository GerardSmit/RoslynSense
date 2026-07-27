using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

public class ProfileRecordingStoreTests
{
    private sealed class FakeRecording : ProfileRecording
    {
        public override ProfileArtifactKind ArtifactKind => ProfileArtifactKind.Speedscope;

        protected override Task<string> StopCoreAsync(CancellationToken cancellationToken) =>
            Task.FromResult("artifact");
    }

    private static FakeRecording Create(ProfileRecordingStore store) => new()
    {
        Id = store.NextId(),
        Description = "test",
        Pid = 1234,
        TempDir = Path.Combine(Path.GetTempPath(), $"rec-test-{Guid.NewGuid():N}"),
        StartedAtUtc = DateTime.UtcNow,
    };

    [Fact]
    public void WhenOneRecordingIsActiveThenResolveWithoutIdReturnsIt()
    {
        using var store = new ProfileRecordingStore();
        var recording = Create(store);
        store.Add(recording);

        var (resolved, error) = store.Resolve(null);

        Assert.Null(error);
        Assert.Same(recording, resolved);
    }

    [Fact]
    public void WhenMultipleRecordingsAreActiveThenResolveWithoutIdFails()
    {
        using var store = new ProfileRecordingStore();
        store.Add(Create(store));
        store.Add(Create(store));

        var (resolved, error) = store.Resolve(null);

        Assert.Null(resolved);
        Assert.Contains("Multiple recordings", error);
    }

    [Fact]
    public void WhenNoRecordingsAreActiveThenResolveExplainsProfileStart()
    {
        using var store = new ProfileRecordingStore();

        var (resolved, error) = store.Resolve(null);

        Assert.Null(resolved);
        Assert.Contains("ProfileStart", error);
    }

    [Fact]
    public void WhenIdIsUnknownThenResolveListsActiveIds()
    {
        using var store = new ProfileRecordingStore();
        var recording = Create(store);
        store.Add(recording);

        var (resolved, error) = store.Resolve("rec-999");

        Assert.Null(resolved);
        Assert.Contains(recording.Id, error);
    }

    [Fact]
    public async Task WhenStoppedTwiceThenTheArtifactIsCollectedOnce()
    {
        using var store = new ProfileRecordingStore();
        var recording = Create(store);
        store.Add(recording);

        var first = await recording.StopAndCollectAsync(CancellationToken.None);
        var second = await recording.StopAndCollectAsync(CancellationToken.None);

        Assert.Equal("artifact", first);
        Assert.Same(first, second);
    }
}

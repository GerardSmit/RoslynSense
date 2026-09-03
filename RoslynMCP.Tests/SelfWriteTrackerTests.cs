using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The record of what this server wrote, which the file watcher consults before treating an event
/// as an outside change.
/// </summary>
/// <remarks>
/// Worth testing directly because its failures are silent in both directions. Suppress too little
/// and every mutating operation costs a second full reload — the reason it exists. Suppress too
/// much and a real edit is discarded with nothing to re-check it, leaving the workspace answering
/// from a project file that no longer exists on disk.
/// </remarks>
public class SelfWriteTrackerTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"roslyn-sense-selfwrite-{Guid.NewGuid():N}");

    public SelfWriteTrackerTests()
    {
        Directory.CreateDirectory(_directory);
        SelfWriteTracker.ResetForTests();
    }

    public void Dispose()
    {
        SelfWriteTracker.ResetForTests();
        try { Directory.Delete(_directory, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task AWriteWeRecordedIsRecognisedAsOurs()
    {
        string path = await WriteAsync("first.csproj", "<Project />");

        SelfWriteTracker.Note(path);

        Assert.True(SelfWriteTracker.WasWrittenByUs(path));
    }

    /// <summary>
    /// An edit that lands after our own write is an outside change, however soon it arrives.
    /// </summary>
    /// <remarks>
    /// This is the direction that matters most. Recognition used to be a time window, which has to
    /// guess how long the watcher will take to report the echo and is wrong both ways: too short
    /// and the redundant reload it exists to prevent happens anyway; too long and a real edit
    /// seconds later is swallowed — with nothing to re-check it, because the project file's own
    /// event was the only thing that would have.
    /// </remarks>
    [Fact]
    public async Task AnEditAfterOurWriteIsNotMistakenForOurs()
    {
        string path = await WriteAsync("second.csproj", "<Project />");
        SelfWriteTracker.Note(path);

        // Someone else writes it — a terminal, another editor, a coding agent.
        await Task.Delay(20);
        await File.WriteAllTextAsync(path, "<Project><!-- theirs --></Project>");

        Assert.False(SelfWriteTracker.WasWrittenByUs(path));
    }

    [Fact]
    public void AFileWeNeverWroteIsNeverOurs()
    {
        Assert.False(SelfWriteTracker.WasWrittenByUs(Path.Combine(_directory, "unwritten.csproj")));
        Assert.False(SelfWriteTracker.WasWrittenByUs(""));
    }

    [Fact]
    public async Task ADeletedFileIsNoLongerOurs()
    {
        string path = await WriteAsync("third.csproj", "<Project />");
        SelfWriteTracker.Note(path);

        File.Delete(path);

        // Its disappearance is an outside change like any other, and one the workspace has to hear
        // about.
        Assert.False(SelfWriteTracker.WasWrittenByUs(path));
    }

    /// <summary>
    /// The record stays bounded, and forgets the oldest writes first.
    /// </summary>
    /// <remarks>
    /// Pruning by "does the stamp still match" removed almost nothing, because a file we wrote and
    /// nobody touched matches forever — so the set only grew, and past its threshold every write
    /// re-stat'd the whole of it. A refactoring renaming several hundred files did that once per
    /// file. Forgetting an old entry is cheap: at worst one redundant reload.
    /// </remarks>
    [Fact]
    public async Task TheRecordIsBoundedAndForgetsTheOldestFirst()
    {
        const int Count = 700;

        string first = await WriteAsync("oldest.csproj", "<Project />");
        SelfWriteTracker.Note(first);

        for (int i = 0; i < Count; i++)
            SelfWriteTracker.Note(await WriteAsync($"bulk{i}.csproj", "<Project />"));

        string last = await WriteAsync("newest.csproj", "<Project />");
        SelfWriteTracker.Note(last);

        // The most recent write is what an echo would be about, so it must still be recognised.
        Assert.True(SelfWriteTracker.WasWrittenByUs(last));

        // And the oldest has been let go rather than the set growing without limit.
        Assert.False(SelfWriteTracker.WasWrittenByUs(first));
    }

    private async Task<string> WriteAsync(string name, string content)
    {
        string path = Path.Combine(_directory, name);
        await File.WriteAllTextAsync(path, content);
        return path;
    }
}

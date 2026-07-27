using RoslynMCP.Services.Designers;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Covers the designer file watcher: which changes it reacts to, and that a failure on a watcher
/// thread is contained rather than escaping into the process.
/// </summary>
public class SolutionSessionServiceTests : IAsyncLifetime
{
    private string _directory = "";

    public Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), "roslynsense-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public void WhenOpenedWithoutWatchThenNoWatcherIsStarted()
    {
        using var session = CreateSession();

        session.Open(Path.Combine(_directory, "App.sln"), [_directory], watch: false);

        Assert.False(session.IsWatching);
        Assert.EndsWith("App.sln", session.SolutionPath);
    }

    [Fact]
    public void WhenOpenedWithWatchThenWatcherIsStarted()
    {
        using var session = CreateSession();

        session.Open(Path.Combine(_directory, "App.sln"), [_directory], watch: true);

        Assert.True(session.IsWatching);
    }

    [Fact]
    public void WhenClosedThenWatchingStopsAndStateIsCleared()
    {
        using var session = CreateSession();
        session.Open(Path.Combine(_directory, "App.sln"), [_directory], watch: true);

        session.Close();

        Assert.False(session.IsWatching);
        Assert.Null(session.SolutionPath);
    }

    [Fact]
    public void WhenDirectoryDoesNotExistThenOpenSucceedsWithoutWatching()
    {
        using var session = CreateSession();

        session.Open(
            Path.Combine(_directory, "App.sln"),
            [Path.Combine(_directory, "does-not-exist")],
            watch: true);

        Assert.False(session.IsWatching);
    }

    [Fact]
    public async Task WhenMarkupChangesThenRegenerationIsAttempted()
    {
        using var session = CreateSession();
        session.Open(Path.Combine(_directory, "App.sln"), [_directory], watch: true);

        // No project surrounds this file, so regeneration fails — but reaching a recorded failure
        // proves the watcher fired, debounced, and handled the error instead of crashing.
        var markup = Path.Combine(_directory, "Page.aspx");
        await File.WriteAllTextAsync(markup, "<%@ Page Language=\"C#\" %>");

        var recorded = await WaitForHistoryAsync(session);

        Assert.True(recorded, "The watcher did not record a regeneration for the changed markup.");
        Assert.Equal(DesignerOutcome.Failed, session.History[^1].Outcome);
    }

    [Fact]
    public async Task WhenGeneratedDesignerIsWrittenThenTheWatcherDoesNotRetrigger()
    {
        using var session = CreateSession();
        session.Open(Path.Combine(_directory, "App.sln"), [_directory], watch: true);

        // Designer files are .cs, which no generator claims — otherwise every write would trigger
        // another regeneration and the watcher would feed itself forever.
        await File.WriteAllTextAsync(
            Path.Combine(_directory, "Page.aspx.designer.cs"), "// generated");
        await File.WriteAllTextAsync(Path.Combine(_directory, "Program.cs"), "// code");

        await Task.Delay(1500);

        Assert.Empty(session.History);
        Assert.Equal(0, session.PendingCount);
    }

    [Fact]
    public async Task WhenChangeIsUnderObjOrBinThenItIsIgnored()
    {
        using var session = CreateSession();
        session.Open(Path.Combine(_directory, "App.sln"), [_directory], watch: true);

        var objDir = Path.Combine(_directory, "obj");
        Directory.CreateDirectory(objDir);
        await File.WriteAllTextAsync(Path.Combine(objDir, "Generated.aspx"), "<%@ Page %>");

        await Task.Delay(1500);

        Assert.Empty(session.History);
    }

    private static SolutionSessionService CreateSession() =>
        new(new DesignerRegenerationService([new AspxDesignerGenerator(), new DbmlDesignerGenerator()]));

    private static async Task<bool> WaitForHistoryAsync(SolutionSessionService session)
    {
        // Filesystem notifications are inherently asynchronous; poll rather than assume a delay.
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (session.History.Count > 0)
                return true;
            await Task.Delay(100);
        }

        return false;
    }
}

using RoslynMCP.Services.HotReload;
using Xunit;

namespace RoslynMCP.Tests;

[Collection(SharedState.Name)]
public class HotReloadLifetimeTests
{
    [Fact]
    public async Task LastOwnerReleaseEndsBaselineAndOldReleaseCannotStopReplacement()
    {
        var path = FixturePaths.SampleProjectFile;
        var (session, _) = await HotReloadService.StartAsync(path, ownerId: "first");
        Assert.NotNull(session);
        try
        {
            var (same, _) = await HotReloadService.StartAsync(path, ownerId: "second");
            Assert.Same(session, same);
            await HotReloadService.ReleaseOwnerAsync(path, "first");
            Assert.True(HotReloadService.IsRunning(path));
            await HotReloadService.ReleaseOwnerAsync(path, "second");
            Assert.False(HotReloadService.IsRunning(path));
            var (replacement, _) = await HotReloadService.StartAsync(path, ownerId: "replacement");
            Assert.NotNull(replacement);
            Assert.NotEqual(session.SessionId, replacement.SessionId);
            await session.StopAsync();
            await HotReloadService.ReleaseOwnerAsync(path, "first");
            Assert.Same(replacement, HotReloadService.Get(path));
        }
        finally { if (HotReloadService.Get(path) is { } live) await live.StopAsync(); }
    }

    [Fact]
    public async Task TenStartApplyStopCyclesLeaveNoSessions()
    {
        var path = FixturePaths.SampleProjectFile;
        for (int i = 0; i < 10; i++)
        {
            var (session, _) = await HotReloadService.StartAsync(path, ownerId: $"cycle:{i}");
            Assert.NotNull(session);
            try { await session.ApplyAsync(); }
            finally { await session.StopAsync(); }
            Assert.False(HotReloadService.IsRunning(path));
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.ApplyAsync());
        }
    }

    [Fact]
    public async Task StopRacingApplyEndsOnceAndRejectsFurtherWork()
    {
        var path = FixturePaths.SampleProjectFile;
        var (session, _) = await HotReloadService.StartAsync(path, ownerId: "race");
        Assert.NotNull(session);
        var apply = session.ApplyAsync();
        await Task.WhenAll(session.StopAsync(), session.StopAsync(), apply);
        Assert.False(HotReloadService.IsRunning(path));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ApplyAsync());
    }

    [Fact]
    public async Task DisconnectReleasesManualOwnerButKeepsALiveProcessOwner()
    {
        var path = FixturePaths.SampleProjectFile;
        var (session, _) = await HotReloadService.StartAsync(path, ownerId: "client:manual");
        Assert.NotNull(session);
        try
        {
            await HotReloadService.StartAsync(path, ownerId: "client:process", ownerPid: Environment.ProcessId);
            await HotReloadService.ReleaseClientAsync("client:");
            Assert.Same(session, HotReloadService.Get(path));
            Assert.Equal(1, session.OwnerCount);
            await HotReloadService.ReleaseOwnerAsync(path, "client:process");
            Assert.False(HotReloadService.IsRunning(path));
        }
        finally { await session.StopAsync(); }
    }

    [Fact]
    public void ProcessStartIdentityDetectsAReusedPid()
    {
        var owner = HotReloadService.SessionOwner.Create("identity", Environment.ProcessId);
        Assert.False(owner.HasExited());
        Assert.True((owner with { Started = owner.Started - 1 }).HasExited());
    }

    [Fact]
    public async Task ExitedTargetReleasesItsBaselineWithoutAnEditorNotification()
    {
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 120" }) start.ArgumentList.Add(arg);
        using var target = System.Diagnostics.Process.Start(start)!;
        HotReloadService? session = null;
        var path = FixturePaths.SampleProjectFile;
        try
        {
            (session, _) = await HotReloadService.StartAsync(path, ownerId: "exited-target", ownerPid: target.Id);
            Assert.NotNull(session);
            target.Kill();
            await target.WaitForExitAsync();
            session.Trim();
            for (int i = 0; i < 100 && HotReloadService.IsRunning(path); i++) await Task.Delay(25);
            Assert.False(HotReloadService.IsRunning(path));
        }
        finally
        {
            if (!target.HasExited) { target.Kill(); await target.WaitForExitAsync(); }
            if (session is not null) await session.StopAsync();
        }
    }

    [Fact]
    public async Task CancelledStartDoesNotPublishASession()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            HotReloadService.StartAsync(FixturePaths.SampleProjectFile, cts.Token, "cancelled"));
        Assert.False(HotReloadService.IsRunning(FixturePaths.SampleProjectFile));
    }
}

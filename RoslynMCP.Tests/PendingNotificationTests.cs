using System.Diagnostics;
using System.Text.Json;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>Per-solution pending message queue + parity with the plugin's node drain hook.</summary>
public class PendingNotificationTests
{
    [Fact]
    public void EnqueueDrainRoundTripThroughSolutionKey()
    {
        string message = $"test message {Guid.NewGuid():N}";

        // Anchor on a project file; drain from the solution directory — both must resolve
        // to the same solution key.
        PendingNotificationStore.Enqueue(FixturePaths.MultiProjectAFile, message);
        var drained = PendingNotificationStore.Drain(FixturePaths.MultiSolutionDir);

        Assert.Contains(message, drained);
        Assert.Empty(PendingNotificationStore.Drain(FixturePaths.MultiSolutionDir));
    }

    [Fact]
    public void NodeDrainHookMatchesServerSolutionKey()
    {
        string hookScript = Path.Combine(FindRepoRoot(), "hooks", "drain-notifications.mjs");
        Assert.True(File.Exists(hookScript), $"hook script not found at {hookScript}");

        string message = $"node parity {Guid.NewGuid():N}";
        PendingNotificationStore.Enqueue(FixturePaths.MultiProjectAFile, message);
        try
        {
            var startInfo = new ProcessStartInfo("node", $"\"{hookScript}\"")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            process!.StandardInput.Write(JsonSerializer.Serialize(
                new { cwd = FixturePaths.MultiSolutionDir }));
            process.StandardInput.Close();

            string stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(15000);

            Assert.Contains(message, stdout);
            using var doc = JsonDocument.Parse(stdout);
            var output = doc.RootElement.GetProperty("hookSpecificOutput");
            Assert.Equal("PreToolUse", output.GetProperty("hookEventName").GetString());
            Assert.Contains("[RoslynSense]", output.GetProperty("additionalContext").GetString());
        }
        finally
        {
            PendingNotificationStore.Drain(FixturePaths.MultiSolutionDir); // clean up on failure
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RoslynMCP.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

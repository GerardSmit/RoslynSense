using System.Diagnostics;
using RoslynMCP.Services;
using RoslynMCP.Services.Run;
using Xunit;

namespace RoslynMCP.Tests;

[Collection(SharedState.Name)]
public class RunningProcessRegistryTests
{
    [Fact]
    public void RegisterListUnregisterRoundTrip()
    {
        var session = new AppSession
        {
            Id = $"test-{Guid.NewGuid():N}",
            ProjectPath = @"C:\fake\Fake.csproj",
            Kind = AppKind.ConsoleApp,
            Process = Process.GetCurrentProcess(), // alive for the duration of the test
            StartedAtUtc = DateTime.UtcNow,
        };

        RunningProcessRegistry.Register(session);
        try
        {
            var entry = RunningProcessRegistry.List().FirstOrDefault(e => e.SessionId == session.Id);
            Assert.NotNull(entry);
            Assert.Equal(Environment.ProcessId, entry!.Pid);
            Assert.Equal(session.ProjectPath, entry.ProjectPath);
        }
        finally
        {
            RunningProcessRegistry.Unregister(session);
        }

        Assert.DoesNotContain(RunningProcessRegistry.List(), e => e.SessionId == session.Id);
    }

    [Fact]
    public void ListPrunesDeadProcesses()
    {
        // Register an entry pointing at a PID that is certainly gone, by writing through a
        // session whose process has exited.
        var dead = Process.Start(OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd", "/c exit 0") { CreateNoWindow = true }
            : new ProcessStartInfo("/bin/sh", "-c \"exit 0\"") { CreateNoWindow = true })!;
        dead.WaitForExit();

        var session = new AppSession
        {
            Id = $"dead-{Guid.NewGuid():N}",
            ProjectPath = @"C:\fake\Dead.csproj",
            Kind = AppKind.ConsoleApp,
            Process = dead,
            StartedAtUtc = DateTime.UtcNow,
        };
        RunningProcessRegistry.Register(session);

        Assert.DoesNotContain(RunningProcessRegistry.List(), e => e.SessionId == session.Id);
    }

    /// <summary>
    /// The editor's own launches arrive as loose values rather than an AppSession, and are
    /// unregistered by session id when the debug session ends.
    /// </summary>
    [Fact]
    public void RegistersAProcessTheDaemonDidNotStart()
    {
        string sessionId = $"editor-{Guid.NewGuid():N}";
        RunningProcessRegistry.Register(
            sessionId, Environment.ProcessId, @"C:\fake\Editor.csproj",
            "http://localhost:5099", DateTime.UtcNow);
        try
        {
            var entry = RunningProcessRegistry.List().FirstOrDefault(e => e.SessionId == sessionId);
            Assert.NotNull(entry);
            Assert.Equal("http://localhost:5099", entry!.Url);
        }
        finally
        {
            RunningProcessRegistry.Unregister(sessionId);
        }

        Assert.DoesNotContain(RunningProcessRegistry.List(), e => e.SessionId == sessionId);
    }

    /// <summary>
    /// Output logged for an app this process did not launch comes back as a tail, so a chat can
    /// read what the user's app printed.
    /// </summary>
    [Fact]
    public void ForeignProcessOutputIsTailed()
    {
        // This process: the log is keyed by pid and only the registry prunes, so a live pid with
        // no registry entry is a stable key that cannot collide with a real app's log.
        int pid = Environment.ProcessId;
        ProcessOutputLog.Delete(pid);
        try
        {
            for (int i = 1; i <= 10; i++)
                ProcessOutputLog.Append(pid, $"line {i}{Environment.NewLine}");

            string tail = ProcessOutputLog.Tail(pid, 3);

            Assert.DoesNotContain("line 7", tail);
            Assert.Contains("line 8", tail);
            Assert.Contains("line 10", tail);
        }
        finally
        {
            ProcessOutputLog.Delete(pid);
        }

        Assert.Equal("", ProcessOutputLog.Tail(pid, 3));
    }

    [Fact]
    public void KillRejectsUnregisteredPid()
    {
        string result = RunningProcessRegistry.Kill(-12345);
        Assert.Contains("No registered process", result);
    }
}

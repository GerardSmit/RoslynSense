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
        var dead = Process.Start(new ProcessStartInfo("cmd", "/c exit 0") { CreateNoWindow = true })!;
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

    [Fact]
    public void KillRejectsUnregisteredPid()
    {
        string result = RunningProcessRegistry.Kill(-12345);
        Assert.Contains("No registered process", result);
    }
}

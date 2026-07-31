using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using RoslynMCP.Services.HotReload;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Edit-and-Continue: the launch preparation that has to happen before a process starts, the
/// module identity a delta is addressed by, and the wire protocol to the in-process agent.
/// </summary>
public class HotReloadTests
{
    // === Launch preparation ===

    [Fact]
    public void TheAgentShipsBesideTheTool()
    {
        // Without it, hot reload on .NET Core is impossible: nothing else can call ApplyUpdate
        // inside the user's process.
        Assert.NotNull(HotReloadLauncher.FindAgent());
    }

    [Fact]
    public void AHotReloadLaunchGetsTheThreeSettingsTheRuntimeReadsAtStartup()
    {
        var startInfo = new ProcessStartInfo();

        Assert.True(HotReloadLauncher.Inject(startInfo));

        Assert.Equal("debug", startInfo.Environment["DOTNET_MODIFIABLE_ASSEMBLIES"]);
        Assert.Contains("RoslynMCP.HotReloadAgent.dll", startInfo.Environment["DOTNET_STARTUP_HOOKS"]);
        Assert.False(string.IsNullOrEmpty(startInfo.Environment["ROSLYNSENSE_HOTRELOAD_PIPE"]));
    }

    [Fact]
    public void AnExistingStartupHookIsKeptRatherThanReplaced()
    {
        // Replacing it would change how the user's app starts, which hot reload has no business
        // doing.
        var startInfo = new ProcessStartInfo();
        startInfo.Environment["DOTNET_STARTUP_HOOKS"] = @"C:\theirs\Hook.dll";

        HotReloadLauncher.Inject(startInfo);

        string hooks = startInfo.Environment["DOTNET_STARTUP_HOOKS"]!;
        Assert.StartsWith(@"C:\theirs\Hook.dll", hooks);
        Assert.Contains("RoslynMCP.HotReloadAgent.dll", hooks);
        Assert.Contains(Path.PathSeparator, hooks);
    }

    // === Module identity ===

    [Fact]
    public void ADeltaIsAddressedByModuleIdRatherThanByName()
    {
        // The same assembly name can be loaded twice; applying a delta to the wrong copy corrupts
        // it, so the MVID read here is what the agent matches on.
        string assembly = typeof(HotReloadTests).Assembly.Location;

        var moduleId = HotReloadService.ReadModuleId(assembly);

        Assert.NotNull(moduleId);
        Assert.Equal(typeof(HotReloadTests).Module.ModuleVersionId, moduleId);
    }

    [Fact]
    public void SomethingThatIsNotAnAssemblyReportsNoModuleIdRatherThanThrowing()
    {
        string path = Path.Combine(Path.GetTempPath(), $"not-an-assembly-{Guid.NewGuid():N}.dll");
        File.WriteAllText(path, "this is not a PE file");

        try
        {
            Assert.Null(HotReloadService.ReadModuleId(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // === Agent protocol ===

    [Fact]
    public async Task AConnectedAgentBecomesAnApplyTargetAndItsCapabilitiesAreReported()
    {
        var server = HotReloadAgentServer.Instance;
        await using var agent = await ConnectAgentAsync(server.PipeName, "SampleApp",
            "Baseline AddMethodToExistingType");

        await WaitForTargetAsync(server, "SampleApp");

        Assert.Contains(server.Targets, t => t.Name == "SampleApp");
        Assert.Contains("Baseline", server.Capabilities());
    }

    [Fact]
    public async Task ADeltaReachesTheAgentIntactAndItsAnswerIsReportedBack()
    {
        var server = HotReloadAgentServer.Instance;
        await using var agent = await ConnectAgentAsync(server.PipeName, "DeltaApp", "Baseline");
        await WaitForTargetAsync(server, "DeltaApp");

        var moduleId = Guid.NewGuid();
        var delta = new HotReloadDelta(moduleId, [1, 2, 3], [4, 5], [6], [7]);

        // The agent side of the exchange: read the update, check it, answer.
        var agentSide = Task.Run(() =>
        {
            using var reader = new BinaryReader(agent, Encoding.UTF8, leaveOpen: true);
            using var writer = new BinaryWriter(agent, Encoding.UTF8, leaveOpen: true);

            Assert.Equal(1, reader.ReadInt32());
            Assert.Equal(moduleId, new Guid(reader.ReadBytes(16)));
            Assert.Equal(new byte[] { 1, 2, 3 }, reader.ReadBytes(reader.ReadInt32()));
            Assert.Equal(new byte[] { 4, 5 }, reader.ReadBytes(reader.ReadInt32()));
            Assert.Equal(new byte[] { 6 }, reader.ReadBytes(reader.ReadInt32()));

            writer.Write(true);
            writer.Write("");
            writer.Flush();
        });

        var (applied, errors) = await server.ApplyAsync([delta]);
        await agentSide;

        Assert.Contains(applied, a => a.Contains("DeltaApp"));
        // Scoped to this agent: the server is process-wide, so another test's target may still be
        // registered, and its failures are not this test's business.
        Assert.DoesNotContain(errors, e => e.Contains("DeltaApp"));
    }

    [Fact]
    public async Task AnAgentThatRejectsTheUpdateIsReportedRatherThanCountedAsApplied()
    {
        var server = HotReloadAgentServer.Instance;
        await using var agent = await ConnectAgentAsync(server.PipeName, "PickyApp", "Baseline");
        await WaitForTargetAsync(server, "PickyApp");

        var agentSide = Task.Run(() =>
        {
            using var reader = new BinaryReader(agent, Encoding.UTF8, leaveOpen: true);
            using var writer = new BinaryWriter(agent, Encoding.UTF8, leaveOpen: true);

            reader.ReadInt32();
            reader.ReadBytes(16);
            for (int block = 0; block < 3; block++)
                reader.ReadBytes(reader.ReadInt32());

            writer.Write(false);
            writer.Write("the runtime refused the update");
            writer.Flush();
        });

        var (applied, errors) = await server.ApplyAsync([
            new HotReloadDelta(Guid.NewGuid(), [1], [2], [3], [])]);
        await agentSide;

        Assert.DoesNotContain(applied, a => a.Contains("PickyApp"));
        Assert.Contains(errors, e => e.Contains("refused the update"));
    }

    private static async Task<NamedPipeClientStream> ConnectAgentAsync(
        string pipeName, string name, string capabilities)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10_000);

        var writer = new BinaryWriter(pipe, Encoding.UTF8, leaveOpen: true);
        writer.Write(1);
        // This process's own id: the server reaps agents whose process is gone, so a fabricated
        // one would be dropped before the test could use it.
        writer.Write(Environment.ProcessId);
        writer.Write(name);
        writer.Write(capabilities);
        writer.Flush();

        return pipe;
    }

    /// <summary>The server registers on its own accept loop, so the handshake lands slightly after
    /// the client's write returns.</summary>
    private static async Task WaitForTargetAsync(HotReloadAgentServer server, string name)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (server.Targets.Any(t => t.Name == name))
                return;
            await Task.Delay(20);
        }

        Assert.Fail($"The agent '{name}' never registered.");
    }
}

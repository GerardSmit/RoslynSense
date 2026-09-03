using System.Diagnostics;
using System.IO.Pipes;
using RoslynMCP.Daemon;
using RoslynMCP.Lsp.Protocol;
using StreamJsonRpc;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The critical integration point of the LSP transport: a daemon connection that sends the
/// <c>Kind == "lsp"</c> handshake must switch from one-shot IPC framing to a long-lived raw
/// LSP JSON-RPC stream on the same pipe. Spawns a real daemon process for the fixture
/// solution and speaks LSP through its pipe.
/// </summary>
public class LspDaemonHandoffTests
{
    [Fact]
    public async Task DaemonConnectionUpgradesToLspSessionAfterHandshake()
    {
        string solutionKey = Path.GetFullPath(FixturePaths.MultiSolutionFile);
        string exePath = typeof(RoslynMCP.Lsp.LspProxy).Assembly.Location;

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = FixturePaths.MultiSolutionDir,
        };
        psi.ArgumentList.Add(exePath);
        psi.ArgumentList.Add("--host");
        psi.ArgumentList.Add(solutionKey);

        using var daemon = Process.Start(psi)!;
        _ = daemon.StandardOutput.ReadToEndAsync();
        _ = daemon.StandardError.ReadToEndAsync();

        try
        {
            // Poll until the daemon's pipe is up (same as DaemonSpawner does).
            string pipeName = HostPaths.PipeName(solutionKey);
            NamedPipeClientStream? pipe = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (pipe is null && DateTime.UtcNow < deadline)
            {
                var candidate = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await candidate.ConnectAsync(250);
                    pipe = candidate;
                }
                catch (Exception ex) when (ex is TimeoutException or IOException)
                {
                    await candidate.DisposeAsync();
                    await Task.Delay(150);
                }
            }
            Assert.NotNull(pipe);

            await using (pipe)
            {
                var handshake = new DaemonRequest("e2e", "", new Dictionary<string, string>(), "", Kind: "lsp");
                await IpcProtocol.WriteMessageAsync(pipe!, handshake, default);

                using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(
                    pipe!, pipe!, new SystemTextJsonFormatter()));
                rpc.StartListening();

                var init = await rpc.InvokeWithParameterObjectAsync<InitializeResult>(
                    "initialize", new { processId = Environment.ProcessId, rootUri = (string?)null })
                    .WaitAsync(TimeSpan.FromSeconds(30));

                Assert.Equal("RoslynSense", init.ServerInfo.Name);
                Assert.True(init.Capabilities.DefinitionProvider);

                await rpc.InvokeAsync<object?>("shutdown");
                await rpc.NotifyAsync("exit");
            }
        }
        finally
        {
            try { daemon.Kill(entireProcessTree: true); } catch { /* already exited */ }
        }
    }
}

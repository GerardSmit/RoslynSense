using System.Diagnostics;
using System.IO.Pipes;

namespace RoslynMCP.Daemon;

/// <summary>
/// <c>roslyn-sense --stop-daemons</c>: asks every shared host on this machine to exit, so a
/// <c>dotnet tool update</c> can uninstall the package.
/// </summary>
/// <remarks>
/// The update fails without this: the daemons (and the standby MSBuild hosts that die with
/// them) run the very binaries the tool store holds, so the uninstall step hits "access to the
/// path is denied" on files that are loaded into live processes. This command runs from that
/// same store — which is fine, because it has exited before the update starts.
///
/// Graceful first (the exit request lets a host dispose its workspaces and withdraw its
/// registry entry), kill as the fallback: a wedged daemon that cannot answer its pipe would
/// otherwise keep the store locked forever, which is the exact situation an update needs to get
/// out of.
/// </remarks>
internal static class DaemonStopper
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(10);

    public static async Task<int> StopAllAsync()
    {
        var hosts = HostRegistry.All();
        if (hosts.Count == 0)
        {
            Console.WriteLine("No RoslynSense daemons are running.");
            return 0;
        }

        foreach (var host in hosts)
        {
            Process? process;
            try
            {
                process = Process.GetProcessById(host.Pid);
            }
            catch (ArgumentException)
            {
                continue; // exited between the listing and now
            }

            using (process)
            {
                string name = Path.GetFileName(host.SolutionPath);
                if (await RequestExitAsync(host) && process.WaitForExit(ExitTimeout))
                {
                    Console.WriteLine($"Stopped the daemon for '{name}' (pid {host.Pid}).");
                    continue;
                }

                try
                {
                    // The standby MSBuild hosts are children and normally die with the daemon;
                    // the tree kill covers them when the daemon has to be killed instead.
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit((int)ExitTimeout.TotalMilliseconds);
                    Console.WriteLine($"Killed the daemon for '{name}' (pid {host.Pid}) — it did not answer its pipe.");
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    Console.WriteLine($"Could not stop the daemon for '{name}' (pid {host.Pid}): {ex.Message}");
                }
            }
        }
        return 0;
    }

    private static async Task<bool> RequestExitAsync(HostRegistry.HostInfo host)
    {
        try
        {
            using var cts = new CancellationTokenSource(ConnectTimeout);
            await using var pipe = new NamedPipeClientStream(
                ".", host.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(cts.Token);

            var request = new DaemonRequest(
                Guid.NewGuid().ToString("N"), Tool: "", Args: [], Format: "markdown", Kind: "exit");
            await IpcProtocol.WriteMessageAsync(pipe, request, cts.Token);
            await IpcProtocol.ReadMessageAsync<DaemonResponse>(pipe, cts.Token);
            return true;
        }
        catch
        {
            return false; // wedged or already gone; the caller escalates
        }
    }
}

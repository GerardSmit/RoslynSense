using System.Text.Json;

namespace RoslynMCP.Daemon;

/// <summary>
/// Advertises a live host to out-of-band readers (today: the tray icon) by dropping a small
/// JSON descriptor beside its lock file.
/// </summary>
/// <remarks>
/// The lock file already proves liveness, but its directory is named by <see cref="HostPaths.Hash"/>
/// — a one-way hash of the solution path — so a reader can count hosts and not name one. Anything
/// that shows the user which solutions are loaded needs the path in plaintext, and the daemon is
/// the only party that has it. The PID is here too so a reader can confirm liveness without
/// contending for the lock: taking the lock exclusively to test it is a race against the daemon's
/// own restart, and a reader that wins it briefly is a reader that lets a second daemon spawn.
/// </remarks>
internal static class HostRegistry
{
    public sealed record HostInfo(
        string SolutionPath,
        int Pid,
        DateTime StartedAtUtc,
        string PipeName,
        string LogPath);

    private static string FileFor(string solutionKey) =>
        Path.Combine(HostPaths.LockDirectory(solutionKey), "host.json");

    public static void Publish(string solutionKey)
    {
        try
        {
            string dir = HostPaths.LockDirectory(solutionKey);
            Directory.CreateDirectory(dir);
            var info = new HostInfo(
                solutionKey,
                Environment.ProcessId,
                DateTime.UtcNow,
                HostPaths.PipeName(solutionKey),
                Path.Combine(dir, "host.log"));
            File.WriteAllText(FileFor(solutionKey), JsonSerializer.Serialize(info));
        }
        catch
        {
            // Advisory only: a host that cannot describe itself still serves.
        }
    }

    /// <summary>
    /// Removes the descriptor on a clean shutdown. A crash leaves it behind on purpose — readers
    /// prune by PID, the same way <c>RunningProcessRegistry</c> does, so there is no state that
    /// only an orderly exit can clean up.
    /// </summary>
    /// <summary>
    /// Every published host whose process is still alive. Descriptors whose PID is gone are
    /// stale crash leftovers and are skipped, matching how <c>Withdraw</c>'s remark says readers
    /// behave.
    /// </summary>
    public static IReadOnlyList<HostInfo> All()
    {
        var hosts = new List<HostInfo>();
        try
        {
            foreach (string file in Directory.EnumerateFiles(
                HostPaths.DaemonRoot, "host.json", SearchOption.AllDirectories))
            {
                try
                {
                    var info = JsonSerializer.Deserialize<HostInfo>(File.ReadAllText(file));
                    if (info is null)
                        continue;
                    using var process = System.Diagnostics.Process.GetProcessById(info.Pid);
                    if (!process.HasExited)
                        hosts.Add(info);
                }
                catch
                {
                    // Unparseable or the process is gone — a crash leftover, not a host.
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // No daemon has ever run on this machine.
        }
        return hosts;
    }

    public static void Withdraw(string solutionKey)
    {
        try { File.Delete(FileFor(solutionKey)); }
        catch { }
    }
}

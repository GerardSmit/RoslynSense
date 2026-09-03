using System.Diagnostics;
using System.Text.Json;

namespace RoslynSense.Tray;

internal sealed record HostEntry(string SolutionPath, int Pid, DateTime StartedAtUtc, string LogPath)
{
    public string SolutionName => Path.GetFileNameWithoutExtension(SolutionPath);
    public string? Directory => Path.GetDirectoryName(SolutionPath);
}

internal sealed record AppEntry(
    string SessionId, int Pid, string ProjectPath, string? Url, DateTime StartedAtUtc, string FilePath)
{
    public string ProjectName => Path.GetFileNameWithoutExtension(ProjectPath);
}

internal sealed record Snapshot(IReadOnlyList<HostEntry> Hosts, IReadOnlyList<AppEntry> Apps)
{
    public bool IsEmpty => Hosts.Count == 0 && Apps.Count == 0;

    public static readonly Snapshot Empty = new([], []);
}

/// <summary>
/// Reads what RoslynSense is currently doing, straight off disk.
/// </summary>
/// <remarks>
/// Both directories below are written by the daemon and are the same source of truth the LSP
/// <c>roslynSense/runningProcesses</c> method serves. Reading the files rather than connecting to
/// a host is what lets one tray icon cover every solution at once: the daemon's pipe name is
/// salted with the build's module id, so it is reachable only by a matching build, whereas these
/// paths are stable. Neither store has a status field — a live PID <em>is</em> the status, so
/// every read prunes what has died.
/// </remarks>
internal static class SenseState
{
    private static readonly JsonSerializerOptions s_json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Per-host lock directories, one per (solution, build).</summary>
    private static string HostDirectory =>
        Path.Combine(Path.GetTempPath(), "roslyn-mcp-daemon");

    private static string RunningDirectory =>
        Path.Combine(Path.GetTempPath(), "roslyn-sense", "running");

    public static string OutputLogFor(int pid) =>
        Path.Combine(Path.GetTempPath(), "roslyn-sense", "output", $"{pid}.log");

    public static Snapshot Scan() => new(ScanHosts(), ScanApps());

    private static List<HostEntry> ScanHosts()
    {
        var hosts = new List<HostEntry>();
        try
        {
            if (!Directory.Exists(HostDirectory))
                return hosts;

            foreach (string dir in Directory.EnumerateDirectories(HostDirectory))
            {
                string file = Path.Combine(dir, "host.json");
                var entry = Read<HostEntry>(file);

                if (entry is null || !IsAlive(entry.Pid))
                {
                    // A crashed daemon leaves its descriptor behind; clean up on the way past.
                    if (entry is not null)
                        TryDelete(file);
                    continue;
                }
                hosts.Add(entry);
            }
        }
        catch
        {
        }

        // Two builds can hold the same solution — dedupe to what the user cares about, the
        // newest host per solution, so the menu shows solutions rather than processes.
        return hosts
            .GroupBy(h => h.SolutionPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(h => h.StartedAtUtc).First())
            .OrderBy(h => h.SolutionName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<AppEntry> ScanApps()
    {
        var apps = new List<AppEntry>();
        try
        {
            if (!Directory.Exists(RunningDirectory))
                return apps;

            foreach (string file in Directory.EnumerateFiles(RunningDirectory, "*.json"))
            {
                var raw = Read<RegistryEntry>(file);
                if (raw is null || !IsAlive(raw.Pid))
                {
                    // Leave pruning of dead entries to the daemon's own registry, which also
                    // deletes the matching output log. Skipping is enough to keep the menu honest.
                    continue;
                }
                apps.Add(new AppEntry(
                    raw.SessionId, raw.Pid, raw.ProjectPath, raw.Url, raw.StartedAtUtc, file));
            }
        }
        catch
        {
        }
        return apps.OrderByDescending(a => a.StartedAtUtc).ToList();
    }

    /// <summary>Mirrors <c>RunningProcessRegistry.Entry</c>, which owns this file format.</summary>
    private sealed record RegistryEntry(
        string SessionId, int Pid, string ProjectPath, string? Url, DateTime StartedAtUtc, int OwnerPid);

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), s_json);
        }
        catch (IOException)
        {
            return null; // mid-write by the daemon — try again next scan
        }
        catch (JsonException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { }
    }

    public static bool IsAlive(int pid)
    {
        if (pid <= 0)
            return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

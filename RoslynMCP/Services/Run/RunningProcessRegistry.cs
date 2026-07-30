using System.Diagnostics;
using System.Text.Json;

namespace RoslynMCP.Services.Run;

/// <summary>
/// Cross-process registry of launched applications. Run sessions are per-chat (the launching
/// MCP client owns the process), but the editor's LSP session lives in the shared daemon —
/// a different process. Each launch drops a small JSON file in a machine-wide temp directory;
/// readers prune entries whose PID is gone, so a crashed owner never leaves ghosts.
/// One file per (owner, session): no cross-process locking needed.
/// </summary>
public static class RunningProcessRegistry
{
    public sealed record Entry(
        string SessionId,
        int Pid,
        string ProjectPath,
        string? Url,
        DateTime StartedAtUtc,
        int OwnerPid);

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = false };

    private static string Directory =>
        Path.Combine(Path.GetTempPath(), "roslyn-sense", "running");

    private static string FileFor(int ownerPid, string sessionId) =>
        Path.Combine(Directory, $"{ownerPid}-{sessionId}.json");

    public static void Register(AppSession session)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var entry = new Entry(
                session.Id, session.Pid, session.ProjectPath, session.Url,
                session.StartedAtUtc, Environment.ProcessId);
            File.WriteAllText(FileFor(entry.OwnerPid, entry.SessionId),
                JsonSerializer.Serialize(entry, s_json));
        }
        catch
        {
            // The registry is advisory (status bar); never fail a launch over it.
        }
    }

    public static void Unregister(AppSession session)
    {
        try { File.Delete(FileFor(Environment.ProcessId, session.Id)); }
        catch { }
    }

    /// <summary>All live entries; files whose process died are deleted on the way through.</summary>
    public static IReadOnlyList<Entry> List()
    {
        var entries = new List<Entry>();
        try
        {
            if (!System.IO.Directory.Exists(Directory))
                return entries;

            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
            {
                Entry? entry = null;
                try
                {
                    entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(file));
                }
                catch (IOException)
                {
                    continue; // mid-write by another process — skip this round
                }
                catch (JsonException)
                {
                }

                if (entry is null || !IsAlive(entry.Pid))
                {
                    try { File.Delete(file); } catch { }
                    continue;
                }
                entries.Add(entry);
            }
        }
        catch
        {
        }
        return entries.OrderByDescending(e => e.StartedAtUtc).ToList();
    }

    /// <summary>Kills a registered process tree by PID. Only PIDs present in the registry are
    /// accepted — this is an editor-facing kill button, not a general process killer.</summary>
    public static string Kill(int pid)
    {
        var entry = List().FirstOrDefault(e => e.Pid == pid);
        if (entry is null)
            return $"No registered process with PID {pid}.";

        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
            // Already gone; fall through to cleanup.
        }
        catch (Exception ex)
        {
            return $"Failed to kill PID {pid}: {ex.Message}";
        }

        try { File.Delete(FileFor(entry.OwnerPid, entry.SessionId)); } catch { }

        // Tell the chat that launched it (via the per-solution queue and the plugin's
        // PreToolUse hook) — otherwise the LLM sees its app gone without explanation.
        string name = Path.GetFileNameWithoutExtension(entry.ProjectPath);
        PendingNotificationStore.Enqueue(entry.ProjectPath,
            $"The user killed the running process '{name}' (pid {pid}, session {entry.SessionId}, " +
            $"project '{entry.ProjectPath}') from the editor. It was not a crash. " +
            "Do not restart it unless the user asks.");
        return $"Killed '{name}' (pid {pid}).";
    }

    private static bool IsAlive(int pid)
    {
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

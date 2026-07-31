using System.Diagnostics;
using System.Text.Json;

namespace RoslynMCP.Services.Debugging;

/// <summary>
/// Cross-process registry of live LLM debug sessions. Debug sessions are per-chat (the MCP
/// client process owns the debugger — <c>[InProcessOnly]</c>), but the editor's LSP session
/// lives in the shared daemon. Each session publishes its state to a per-owner-PID JSON file
/// so the editor can mirror it (paused location, reason) and drive it through the owner's
/// command pipe (<see cref="DebugCommandPipeServer"/>). Readers prune entries whose owner
/// process died, mirroring <see cref="Run.RunningProcessRegistry"/>.
/// </summary>
public static class DebugStateStore
{
    public sealed record Breakpoint(int Id, string File, int Line, string? Condition);

    public sealed record Entry(
        int OwnerPid,
        string PipeName,
        string Kind,       // "test" | "attach"
        string Target,     // csproj path or attached pid
        string State,      // "running" | "stopped" | "exited"
        string? Reason,    // breakpoint-hit, step, ...
        string? Function,
        string? FilePath,
        int Line,
        DateTime UpdatedAtUtc,
        IReadOnlyList<Breakpoint>? Breakpoints = null);

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = false };

    private static string Directory =>
        Path.Combine(Path.GetTempPath(), "roslyn-sense", "debug");

    private static string FileFor(int ownerPid) =>
        Path.Combine(Directory, $"{ownerPid}.json");

    /// <summary>The command pipe name for a debug session owned by <paramref name="ownerPid"/>.</summary>
    public static string PipeNameFor(int ownerPid) => $"roslyn-sense-debug-{ownerPid}";

    public static void Publish(Entry entry)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FileFor(entry.OwnerPid), JsonSerializer.Serialize(entry, s_json));
        }
        catch
        {
            // Advisory (editor mirror); never fail the debugger over it.
        }
    }

    public static void Clear(int ownerPid)
    {
        try { File.Delete(FileFor(ownerPid)); }
        catch { }
    }

    /// <summary>All live entries; files whose owner process died are deleted on the way through.</summary>
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
                    continue; // mid-write by the owner — skip this round
                }
                catch (JsonException)
                {
                }

                if (entry is null || !IsAlive(entry.OwnerPid))
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
        return entries.OrderByDescending(e => e.UpdatedAtUtc).ToList();
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

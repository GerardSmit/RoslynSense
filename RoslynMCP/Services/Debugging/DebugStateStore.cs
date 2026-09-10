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
        IReadOnlyList<Breakpoint>? Breakpoints = null,
        // Numbers the stops, so a mirror can tell a fresh stop on the same line (a loop
        // iteration) from the one it already showed. 0 when the engine does not count.
        long StopSequence = 0);

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = false };

    private static string Directory =>
        Path.Combine(Path.GetTempPath(), "roslyn-sense", "debug");

    private static string FileFor(int ownerPid) =>
        Path.Combine(Directory, $"{ownerPid}.json");

    private static string TraceFileFor(int ownerPid) =>
        Path.Combine(Directory, $"{ownerPid}.log");

    /// <summary>How much of a session's trace is kept before it starts again from empty.</summary>
    private const long MaxTraceBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Appends one line to the session's trace, beside its state file.
    /// </summary>
    /// <remarks>
    /// The engine's own account of itself — the module it could not find symbols for, the edit it
    /// queued and why, the stop it declined to apply at — used to reach the debug console and
    /// nowhere else. That console belongs to whichever client happened to be attached, so a
    /// session that went wrong left nothing behind: the first question after "hot reload did not
    /// work" is what the engine decided, and until this the honest answer was that nobody had
    /// written it down. Truncated rather than rotated, because this is a tail and not an archive,
    /// and best-effort throughout — a debugger must not fail over its own logging.
    /// </remarks>
    public static void Trace(int ownerPid, string line)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var path = TraceFileFor(ownerPid);
            var file = new FileInfo(path);
            if (file.Exists && file.Length > MaxTraceBytes)
                File.WriteAllText(path, string.Empty);
            File.AppendAllText(
                path, $"{DateTime.Now:HH:mm:ss.fff} {line.ReplaceLineEndings(" ")}{Environment.NewLine}");
        }
        catch
        {
            // Advisory; never fail the debugger over it.
        }
    }

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
        // The trace outlives the state file on purpose: a session that ended badly is exactly the
        // one somebody wants to read afterwards, and List() prunes only the JSON.
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

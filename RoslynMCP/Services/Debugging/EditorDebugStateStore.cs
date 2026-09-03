using System.Text.Json;
using RoslynMCP.Daemon;

namespace RoslynMCP.Services.Debugging;

/// <summary>
/// The editor's own debug session (VSCode's C# debugger), mirrored for LLM clients.
/// The extension tracks its debug adapter and reports transitions to the daemon, which writes
/// them here; MCP debug tools and the plugin's prompt hook read the file to tell the LLM the
/// user is paused at a breakpoint — and to route debug commands into the editor's session.
/// Keyed by the build-independent solution hash (<see cref="HostPaths.SolutionHash"/>), which
/// the hook script derives in JavaScript.
/// </summary>
public static class EditorDebugStateStore
{
    public sealed record State(
        bool Active,
        string? SessionName,
        string? AdapterType,   // e.g. "coreclr"
        string ExecutionState, // "stopped" | "running"
        string? Reason,        // breakpoint, step, exception, ...
        string? FilePath,
        int Line,
        DateTime UpdatedAtUtc);

    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    public static void Write(string solutionPath, State state)
    {
        try
        {
            string file = FileFor(solutionPath);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(state, s_json));
        }
        catch
        {
            // Advisory mirror — never fail the reporter over it.
        }
    }

    public static State? Read(string solutionPath)
    {
        try
        {
            string file = FileFor(solutionPath);
            if (!File.Exists(file))
                return null;
            return JsonSerializer.Deserialize<State>(File.ReadAllText(file), s_json);
        }
        catch
        {
            return null;
        }
    }

    public static void Clear(string solutionPath)
    {
        try { File.Delete(FileFor(solutionPath)); }
        catch { }
    }

    /// <summary>Resolves the state for the solution owning <paramref name="anchorPath"/> —
    /// how MCP tools (whose anchor is the working directory) find the editor session.</summary>
    public static State? ReadNearest(string anchorPath)
    {
        string? solution = PathHelper.FindNearestSolution(anchorPath);
        return solution is null ? null : Read(solution);
    }

    private static string FileFor(string solutionPath) =>
        Path.Combine(Path.GetTempPath(), "roslyn-sense", "editor-debug",
            HostPaths.SolutionHash(Path.GetFullPath(solutionPath)) + ".json");
}

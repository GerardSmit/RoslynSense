using System.Text.Json;
using RoslynMCP.Daemon;

namespace RoslynMCP.Services.Debugging;

/// <summary>
/// The solution's breakpoint set, persisted independently of any debug session. The editor
/// mirrors its breakpoint model here on every change (via the daemon); chat-owned debug
/// sessions apply the set when they start. This is what makes a breakpoint removed in the
/// editor while NO session runs stay removed in the next AI session — there is one shared
/// set, not per-session copies. Keyed by solution hash like the other bridge stores.
/// </summary>
public static class SharedBreakpointStore
{
    public sealed record Breakpoint(string File, int Line, string? Condition);

    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    public static void Write(string solutionPath, IReadOnlyList<Breakpoint> breakpoints)
    {
        try
        {
            string file = FileFor(solutionPath);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(breakpoints, s_json));
        }
        catch
        {
            // Advisory mirror — never fail the reporter over it.
        }
    }

    public static IReadOnlyList<Breakpoint> Read(string solutionPath)
    {
        try
        {
            string file = FileFor(solutionPath);
            if (!File.Exists(file))
                return [];
            return JsonSerializer.Deserialize<List<Breakpoint>>(File.ReadAllText(file), s_json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>The set for the solution owning <paramref name="anchorPath"/> — how debug
    /// tools (anchored on the working directory) find the editor's breakpoints.</summary>
    public static IReadOnlyList<Breakpoint> ReadNearest(string anchorPath)
    {
        string? solution = PathHelper.FindNearestSolution(anchorPath);
        return solution is null ? [] : Read(solution);
    }

    private static string FileFor(string solutionPath) =>
        Path.Combine(Path.GetTempPath(), "roslyn-sense", "breakpoints",
            HostPaths.Hash(Path.GetFullPath(solutionPath)) + ".json");
}

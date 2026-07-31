using System.Text.Json;
using RoslynMCP.Daemon;

namespace RoslynMCP.Services;

/// <summary>
/// What the user is currently looking at, mirrored for LLM clients.
///
/// The debug bridge told the AI what the user is <em>debugging</em>; this tells it what the
/// user is <em>reading</em>, so "why does this fail?" resolves to the file and method actually
/// on screen instead of guessing. The extension publishes on a debounce; MCP tools read it.
/// Keyed by the same solution hash as the shared daemon.
/// </summary>
public static class EditorContextStore
{
    public sealed record VisibleDiagnostic(string Severity, string? Code, string Message, int Line);

    public sealed record Context(
        string? ActiveFile,
        int Line,
        int Character,
        string? EnclosingSymbol,
        string? SelectionText,
        IReadOnlyList<string> OpenFiles,
        IReadOnlyList<string> DirtyFiles,
        IReadOnlyList<VisibleDiagnostic> Diagnostics,
        DateTime UpdatedAtUtc);

    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    public static void Write(string solutionPath, Context context)
    {
        try
        {
            string file = FileFor(solutionPath);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(context, s_json));
        }
        catch
        {
            // Advisory mirror — never fail the reporter over it.
        }
    }

    public static Context? Read(string solutionPath)
    {
        try
        {
            string file = FileFor(solutionPath);
            return File.Exists(file)
                ? JsonSerializer.Deserialize<Context>(File.ReadAllText(file), s_json)
                : null;
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

    /// <summary>Resolves the context for the solution owning <paramref name="anchorPath"/> —
    /// how MCP tools (whose anchor is the working directory) find the editor.</summary>
    public static Context? ReadNearest(string anchorPath)
    {
        string? solution = PathHelper.FindNearestSolution(anchorPath);
        return solution is null ? null : Read(solution);
    }

    private static string FileFor(string solutionPath) =>
        Path.Combine(Path.GetTempPath(), "roslyn-sense", "editor-context",
            HostPaths.Hash(Path.GetFullPath(solutionPath)) + ".json");
}

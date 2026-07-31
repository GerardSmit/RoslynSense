using System.Text.Json;
using System.Text.RegularExpressions;

namespace RoslynMCP.Services.Testing;

/// <summary>Where a failure actually happened, recovered from its stack trace.</summary>
public sealed record FailureLocation(string FilePath, int Line);

/// <summary>
/// The last few test runs, kept so a failure can be asked about after the fact.
/// </summary>
/// <remarks>
/// On disk rather than in memory because the run and the question about it usually come from
/// different processes: `run_tests` executes in an MCP client, the editor's Test Explorer runs
/// in the daemon, and a chat asking "why did that fail" is a third. Scoped per solution, in the
/// user's temp directory, exactly like the debug stores.
/// </remarks>
public static partial class TestRunStore
{
    /// <summary>Enough history to ask about the run before last, without unbounded growth.</summary>
    private const int MaxRuns = 10;

    public sealed record Run(
        string RunId,
        string ProjectPath,
        DateTime StartedAtUtc,
        IReadOnlyList<TestResult> Results);

    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    [GeneratedRegex(@"\sin\s(?<file>[A-Za-z]:\\[^\r\n]+?|/[^\r\n]+?):line\s(?<line>\d+)")]
    private static partial Regex StackFrameRegex();

    public static string Record(string solutionPath, string projectPath, IReadOnlyList<TestResult> results)
    {
        string runId = Guid.NewGuid().ToString("N")[..8];
        try
        {
            var runs = Read(solutionPath).ToList();
            runs.Insert(0, new Run(runId, projectPath, DateTime.UtcNow, results));

            string file = FileFor(solutionPath);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(runs.Take(MaxRuns).ToList(), s_json));
        }
        catch (Exception ex)
        {
            // Losing the history is not worth failing a test run over.
            ServiceLog.Warn($"Could not record the test run: {ex.Message}", key: "test-run-store");
        }
        return runId;
    }

    public static IReadOnlyList<Run> Read(string solutionPath)
    {
        try
        {
            string file = FileFor(solutionPath);
            return File.Exists(file)
                ? JsonSerializer.Deserialize<List<Run>>(File.ReadAllText(file), s_json) ?? []
                : [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>A run by id, or the most recent one when no id is given.</summary>
    public static Run? Find(string solutionPath, string? runId = null)
    {
        var runs = Read(solutionPath);
        return string.IsNullOrWhiteSpace(runId)
            ? runs.FirstOrDefault()
            : runs.FirstOrDefault(r => r.RunId.Equals(runId, StringComparison.OrdinalIgnoreCase));
    }

    public static void Clear(string solutionPath)
    {
        try
        {
            string file = FileFor(solutionPath);
            if (File.Exists(file))
                File.Delete(file);
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// The deepest frame in the test's own code, which is where the assertion actually sits —
    /// the top frame belongs to the assertion library.
    /// </summary>
    public static FailureLocation? LocateFailure(TestResult result)
    {
        if (string.IsNullOrEmpty(result.StackTrace))
            return null;

        FailureLocation? firstWithSource = null;

        foreach (Match match in StackFrameRegex().Matches(result.StackTrace))
        {
            string file = match.Groups["file"].Value.Trim();
            if (!int.TryParse(match.Groups["line"].Value, out int line))
                continue;

            var location = new FailureLocation(file, line);
            firstWithSource ??= location;

            // Frames are listed innermost first; the first one whose file exists locally is in
            // the user's code, since the framework's sources are not on this machine.
            if (File.Exists(file))
                return location;
        }

        return firstWithSource;
    }

    /// <summary>The runs recorded for the solution nearest an anchor path, for callers that
    /// only know their working directory.</summary>
    public static Run? FindNearest(string anchorPath, string? runId = null)
    {
        string? solution = PathHelper.FindNearestSolution(anchorPath);
        return solution is null ? null : Find(solution, runId);
    }

    private static string FileFor(string solutionPath) =>
        Path.Combine(
            Path.GetTempPath(), "roslyn-sense", "test-runs",
            Daemon.HostPaths.Hash(Path.GetFullPath(solutionPath)) + ".json");
}

using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using RoslynMCP.Services;
using RoslynMCP.Services.Testing;

namespace RoslynMCP.Tools;

[McpServerToolType]
public static class TestFailuresTool
{
    /// <summary>Failures from a completed run, with their assertion sites resolved.</summary>
    [McpServerTool, Description(
        "Get the failures from the last test run (or a specific run id), each with the file and " +
        "line of the failing assertion resolved from its stack trace. Use this after run_tests " +
        "instead of re-reading its output, and to jump straight to the assertion rather than " +
        "searching for the test by name.")]
    public static async Task<string> GetTestFailures(
        IOutputFormatter fmt,
        [Description("Run id from run_tests. Omit for the most recent run.")]
        string? runId = null,
        [Description("Maximum failures to report (default: 20).")] int maxResults = 20,
        CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;

        var run = TestRunStore.FindNearest(Directory.GetCurrentDirectory(), runId);
        if (run is null)
        {
            return runId is null
                ? "No test run has been recorded for this solution yet. Use run_tests first."
                : $"No run with id '{runId}'. Omit the id for the most recent run.";
        }

        var failures = run.Results.Where(r => r.Failed).ToList();
        if (failures.Count == 0)
        {
            return $"Run {run.RunId} ({Path.GetFileNameWithoutExtension(run.ProjectPath)}, " +
                   $"{run.Results.Count} tests) had no failures.";
        }

        var sb = new StringBuilder();
        fmt.AppendHeader(sb, $"{failures.Count} failing test{(failures.Count == 1 ? "" : "s")}");
        fmt.AppendField(sb, "Run", $"{run.RunId} — {Path.GetFileName(run.ProjectPath)}, " +
                                   $"{run.StartedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}");

        foreach (var failure in failures.Take(Math.Max(1, maxResults)))
        {
            sb.AppendLine();
            fmt.AppendHeader(sb, failure.FullyQualifiedName, 2);

            if (TestRunStore.LocateFailure(failure) is { } location)
                fmt.AppendField(sb, "At", $"{location.FilePath}:{location.Line}");

            if (failure.ErrorMessage is { Length: > 0 } message)
            {
                sb.AppendLine();
                sb.AppendLine("```");
                sb.AppendLine(Truncate(message, 1500));
                sb.AppendLine("```");
            }
        }

        if (failures.Count > maxResults)
            sb.AppendLine($"\n…and {failures.Count - maxResults} more.");

        fmt.AppendHints(sb,
            "The 'At' line is the deepest frame in your own code, not the assertion library",
            "Use run_tests with a filter to re-run one of these");

        return sb.ToString();
    }

    private static string Truncate(string text, int limit) =>
        text.Length <= limit ? text.TrimEnd() : text[..limit].TrimEnd() + "\n…truncated";
}

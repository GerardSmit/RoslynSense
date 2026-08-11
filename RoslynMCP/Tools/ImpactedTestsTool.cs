using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using RoslynMCP.Services;
using RoslynMCP.Services.Testing;

namespace RoslynMCP.Tools;

/// <summary>
/// Runs only the tests the working copy's own changes can affect, and builds the per-test
/// coverage map that makes that selection possible.
/// </summary>
[McpServerToolType]
public static class ImpactedTestsTool
{
    [McpServerTool, Description(
        "Run only the tests affected by the current git changes, instead of the whole suite. " +
        "Uses the per-test coverage map (build it with BuildCoverageMap) to find which tests " +
        "execute the changed lines, and falls back to walking references for code the map does " +
        "not cover yet — new code, typically. Set dryRun=true to see the selection without " +
        "running anything.")]
    public static async Task<string> RunImpactedTests(
        [Description("Path to the solution, a project, or any file inside the repository.")]
        string path,
        IOutputFormatter fmt,
        [Description("Which changes to consider: 'uncommitted' (staged and unstaged against HEAD, " +
                     "the default), 'branch' (everything since the merge base with main), or " +
                     "'ref' (against the revision given in gitRef).")]
        string scope = "uncommitted",
        [Description("Git revision to compare against. Only used when scope is 'ref'.")]
        string? gitRef = null,
        [Description("List the selected tests and why they were selected, without running them.")]
        bool dryRun = false,
        [Description("Whether to build before running. Default is true.")]
        bool build = true,
        [Description("Timeout in seconds for each project's test run. Default is 600.")]
        int timeoutSeconds = 600,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TryParseScope(scope, out var parsedScope, out string? scopeError))
                return $"Error: {scopeError}";

            var selection = await TestImpactService.SelectAsync(
                PathHelper.NormalizePath(path), parsedScope, gitRef, ct: cancellationToken);

            if (selection.Error is not null)
                return $"Error: {selection.Error}";

            var sb = new StringBuilder();
            AppendSelection(sb, fmt, selection);

            if (selection.Tests.Count == 0 || dryRun)
                return sb.ToString();

            fmt.AppendSeparator(sb);

            foreach (var group in selection.ByProject())
            {
                if (string.IsNullOrEmpty(group.Key))
                {
                    fmt.AppendHeader(sb, "Skipped: tests with no known project", 2);
                    foreach (var test in group)
                        sb.AppendLine($"- {test.FullyQualifiedName}");
                    continue;
                }

                var names = group.Select(t => t.FullyQualifiedName).ToList();
                string? filter = TestRunService.BuildFilter(names);

                fmt.AppendHeader(sb, $"{Path.GetFileNameWithoutExtension(group.Key)} — {names.Count} test(s)", 2);

                var outcome = await TestRunService.RunAsync(
                    group.Key, filter, build, timeoutSeconds, cancellationToken);

                if (outcome.Error is not null)
                {
                    sb.AppendLine($"Run failed: {outcome.Error}");
                    continue;
                }

                int passed = outcome.Results.Count(r => r.Outcome == "Passed");
                var failed = outcome.Results.Where(r => r.Failed).ToList();

                fmt.AppendField(sb, "Passed", passed);
                if (failed.Count > 0)
                    fmt.AppendField(sb, "Failed", failed.Count);

                foreach (var failure in failed)
                {
                    fmt.AppendHeader(sb, fmt.Escape(failure.FullyQualifiedName), 3);
                    if (!string.IsNullOrWhiteSpace(failure.ErrorMessage))
                    {
                        sb.AppendLine("```");
                        sb.AppendLine(failure.ErrorMessage.Trim());
                        sb.AppendLine("```");
                    }
                }
            }

            return sb.ToString();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description(
        "Build the per-test coverage map: which tests execute which lines. Runs coverage once " +
        "per test class, so the first build is slow; later builds only re-run classes whose " +
        "source changed. Needed by RunImpactedTests and by the editor's per-method test counts.")]
    public static async Task<string> BuildCoverageMap(
        [Description("Path to the test project (.csproj) or a source file inside it.")]
        string projectPath,
        IOutputFormatter fmt,
        [Description("Only map test classes whose full name contains this text. " +
                     "Useful for extending an existing map one area at a time.")]
        string? classFilter = null,
        [Description("Re-run every class, ignoring entries that are still up to date.")]
        bool force = false,
        [Description("Timeout in seconds for each class's coverage run. Default is 300.")]
        int timeoutSecondsPerClass = 300,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await TestCoverageMapBuilder.BuildAsync(
                PathHelper.NormalizePath(projectPath), force, classFilter,
                timeoutSecondsPerClass, cancellationToken);

            if (result.Error is not null)
                return $"Error: {result.Error}";

            var sb = new StringBuilder();
            fmt.AppendHeader(sb, "Coverage map built");
            fmt.AppendField(sb, "Classes run", result.ClassesRun);
            fmt.AppendField(sb, "Classes reused", result.ClassesReused);
            fmt.AppendField(sb, "Tests mapped", result.Map.TestCount);
            fmt.AppendField(sb, "Files covered", result.Map.CoveredFiles().Count);

            if (result.Failures.Count > 0)
            {
                fmt.AppendSeparator(sb);
                fmt.AppendHeader(sb, $"{result.Failures.Count} class(es) could not be mapped", 2);
                foreach (string failure in result.Failures.Take(20))
                    sb.AppendLine($"- {failure}");
            }

            fmt.AppendHints(sb,
                "Use RunImpactedTests to run only what your changes affect",
                "Re-run this after large refactors; small edits are handled incrementally");

            return sb.ToString();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static void AppendSelection(StringBuilder sb, IOutputFormatter fmt, TestImpactSelection selection)
    {
        fmt.AppendHeader(sb, "Impacted tests");
        fmt.AppendField(sb, "Comparing", selection.Description);
        fmt.AppendField(sb, "Changed files", selection.ChangedFiles.Count);
        fmt.AppendField(sb, "Selected tests", selection.Tests.Count);

        if (selection.MapWasEmpty)
        {
            sb.AppendLine();
            sb.AppendLine(
                "No coverage map exists yet, so the selection comes from reference walking alone. " +
                "Run BuildCoverageMap for selections that see runtime-only call paths.");
        }

        if (selection.Tests.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine(selection.ChangedFiles.Count == 0
                ? "Nothing has changed."
                : "No tests reach the changed code.");
        }

        foreach (var group in selection.Tests.GroupBy(t => t.Reason))
        {
            fmt.AppendHeader(sb, Describe(group.Key), 2);
            foreach (var test in group.Take(50))
                sb.AppendLine($"- {test.FullyQualifiedName}");
            if (group.Count() > 50)
                sb.AppendLine($"- …and {group.Count() - 50} more");
        }

        if (selection.UncoveredFiles.Count > 0)
        {
            fmt.AppendHeader(sb, "Changed with no test reaching them", 2);
            foreach (string file in selection.UncoveredFiles.Take(20))
                sb.AppendLine($"- {Path.GetFileName(file)}");
        }
    }

    private static string Describe(ImpactReason reason) => reason switch
    {
        ImpactReason.CoveredChangedLines => "Cover the changed lines",
        ImpactReason.CoveredChangedFile => "Cover the changed file (its lines have moved since the map was built)",
        ImpactReason.TestChanged => "The test itself changed",
        _ => "Reference the changed code (no coverage for it yet)",
    };

    private static bool TryParseScope(string scope, out GitChangeScope parsed, out string? error)
    {
        error = null;
        switch (scope?.Trim().ToLowerInvariant())
        {
            case null or "" or "uncommitted" or "working" or "local":
                parsed = GitChangeScope.Uncommitted;
                return true;
            case "branch":
                parsed = GitChangeScope.Branch;
                return true;
            case "ref" or "reference":
                parsed = GitChangeScope.Ref;
                return true;
            default:
                parsed = GitChangeScope.Uncommitted;
                error = $"Unknown scope '{scope}'. Use 'uncommitted', 'branch', or 'ref'.";
                return false;
        }
    }
}

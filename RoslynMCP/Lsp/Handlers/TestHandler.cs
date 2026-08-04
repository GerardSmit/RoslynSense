using System.Collections.Concurrent;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Services.Testing;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>
/// The Test Explorer's server side. Discovery runs against the loaded compilation rather than
/// a separate `dotnet test --list-tests` process, so it is fast and sees unsaved buffers;
/// running and debugging reuse the same services the MCP test tools use.
/// </summary>
internal static class TestHandler
{
    public static async Task<TestProjectInfo[]> ProjectsAsync(CancellationToken ct)
    {
        var projects = await TestDiscoveryService.FindTestProjectsAsync(ct);
        return projects.Select(p => new TestProjectInfo(p.ProjectPath, p.ProjectName)).ToArray();
    }

    public static async Task<TestInfo[]> DiscoverAsync(TestDiscoverParams p, CancellationToken ct)
    {
        string? projectPath = p.ProjectPath;
        string? sourceFilter = null;

        if (projectPath is null && p.Uri is not null)
        {
            // Discovery scoped to one file: resolve its project, then filter to that file.
            sourceFilter = LspConverters.UriToPath(p.Uri);
            projectPath = await WorkspaceService.FindContainingProjectAsync(sourceFilter, ct);
        }

        if (string.IsNullOrEmpty(projectPath))
            return [];

        var tests = await TestDiscoveryService.DiscoverAsync(
            projectPath, classNameFilter: null, sourceFilter, ct);

        return tests.Select(t => new TestInfo(
            t.Id, t.FullyQualifiedName, t.DisplayName, t.ClassName, t.Namespace,
            t.Framework, t.FilePath, t.StartLine, t.EndLine, t.ProjectPath)).ToArray();
    }

    /// <summary>
    /// Runs in flight, so a client can cancel one it started.
    /// </summary>
    /// <remarks>
    /// A test run outlives the request that started it in every way that matters: it holds a
    /// build lock and a test host. Cancelling has to reach the process, not just the await, so
    /// the token that kills it lives here rather than being the request's own.
    /// </remarks>
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> s_runs =
        new(StringComparer.Ordinal);

    public static async Task<TestRunResponse> RunAsync(TestRunParams p, CancellationToken ct)
    {
        string label = Path.GetFileNameWithoutExtension(p.ProjectPath);
        await using var progress = await ProgressReporter.BeginAsync($"Running tests in {label}", ct);

        string? filter = TestRunService.BuildFilter(p.FullyQualifiedNames ?? []);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (p.RunId is { Length: > 0 } runId)
            s_runs[runId] = cancellation;

        try
        {
            if (p.CollectCoverage)
            {
                var coverage = await CoverageService.RunCoverageAsync(
                    p.ProjectPath, filter, timeoutSeconds: 600, cancellation.Token);
                // Coverage runs the tests too, but reports through the coverage pipeline; the
                // run results still come from the TRX the same invocation wrote.
                if (!coverage.Success)
                    Console.Error.WriteLine($"[Lsp] Coverage run failed: {coverage.Message}");
            }

            var outcome = await TestRunService.RunAsync(
                p.ProjectPath, filter, build: true, timeoutSeconds: 600, cancellation.Token,
                onProgress: p.RunId is { Length: > 0 } id ? e => Publish(id, e) : null);

            // Into the run's own output as well as back to the caller: the Test Results terminal
            // is where someone looking at a run that did nothing will actually be looking.
            if (outcome.Error is not null && p.RunId is { Length: > 0 } failed)
                Publish(failed, new TestProgress("output", Message: outcome.Error));

            return new TestRunResponse(
                [.. outcome.Results.Select(r => new TestResultInfo(
                    r.FullyQualifiedName, r.Outcome, r.DurationMs, r.ErrorMessage, r.StackTrace))],
                outcome.Error);
        }
        finally
        {
            if (p.RunId is { Length: > 0 } finished)
                s_runs.TryRemove(finished, out _);
        }
    }

    /// <summary>Stops a run that is still going. Unknown ids are a no-op — the run finished.</summary>
    public static void Cancel(TestCancelParams p)
    {
        if (s_runs.TryGetValue(p.RunId, out var cancellation))
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { /* finished between the lookup and here */ }
        }
    }

    /// <summary>
    /// Sends one progress event to every connected editor, and drops it if that fails.
    /// </summary>
    /// <remarks>
    /// Called on the test process's output thread, so it must not block and must not throw:
    /// an exception here would tear down the reader and lose the rest of the output.
    /// </remarks>
    private static void Publish(string runId, TestProgress e)
    {
        var notification = new TestRunEvent(
            runId, e.Kind, e.FullyQualifiedName, e.Message, e.DurationMs);

        foreach (var rpc in LspSessionRegistry.ActiveSessions())
        {
            try
            {
                _ = rpc.NotifyWithParameterObjectAsync("roslynSense/testRunEvent", notification);
            }
            catch
            {
                // Session gone. The final results still arrive over the request itself.
            }
        }
    }

    public static async Task<TestDebugResult> DebugAsync(TestDebugParams p, CancellationToken ct)
    {
        await using var progress = await ProgressReporter.BeginAsync("Starting test host", ct);

        string? filter = TestRunService.BuildFilter(p.FullyQualifiedNames ?? []);
        var (processId, error) = await TestRunService.StartForDebugAsync(p.ProjectPath, filter, ct);
        return new TestDebugResult(processId, error);
    }

    /// <summary>
    /// The coverage the last run collected, provided it was for this project.
    /// </summary>
    /// <remarks>
    /// The project check is the point: the cache holds one result for the whole process, so
    /// running the Coverage profile on project B right after project A would otherwise paint
    /// A's gutters onto B's files and call it a measurement.
    /// </remarks>
    public static FileCoverageInfo[] Coverage(TestCoverageParams p)
    {
        var data = CoverageService.GetCachedCoverage(out string? collectedFor, out _);
        if (data is null)
            return [];

        if (collectedFor is { Length: > 0 }
            && !PathHelper.NormalizePath(collectedFor)
                .Equals(PathHelper.NormalizePath(p.ProjectPath), StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return data.Files.Values
            .Select(file => new FileCoverageInfo(
                file.FilePath,
                file.Lines.Values
                    .Select(ToLineInfo)
                    .OrderBy(line => line.Line)
                    .ToArray()))
            .Where(file => file.Lines.Length > 0)
            .ToArray();
    }

    /// <summary>
    /// Cobertura reports conditions as the string "50% (1/2)"; the counts are what the editor's
    /// coverage view needs, so they are pulled back out of it.
    /// </summary>
    private static LineCoverageInfo ToLineInfo(LineCoverage line)
    {
        if (!line.IsBranch || line.ConditionCoverage is not { } condition)
            return new LineCoverageInfo(line.LineNumber, line.Hits);

        int open = condition.IndexOf('(');
        int slash = condition.IndexOf('/');
        int close = condition.IndexOf(')');
        if (open < 0 || slash <= open || close <= slash
            || !int.TryParse(condition[(open + 1)..slash], out int covered)
            || !int.TryParse(condition[(slash + 1)..close], out int total))
        {
            return new LineCoverageInfo(line.LineNumber, line.Hits);
        }

        return new LineCoverageInfo(line.LineNumber, line.Hits, covered, total);
    }
}

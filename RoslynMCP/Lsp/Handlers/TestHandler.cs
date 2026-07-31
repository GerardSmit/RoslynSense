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

    public static async Task<TestResultInfo[]> RunAsync(TestRunParams p, CancellationToken ct)
    {
        string label = Path.GetFileNameWithoutExtension(p.ProjectPath);
        await using var progress = await ProgressReporter.BeginAsync($"Running tests in {label}", ct);

        string? filter = TestRunService.BuildFilter(p.FullyQualifiedNames ?? []);

        if (p.CollectCoverage)
        {
            var coverage = await CoverageService.RunCoverageAsync(
                p.ProjectPath, filter, timeoutSeconds: 600, ct);
            // Coverage runs the tests too, but reports through the coverage pipeline; the run
            // results still come from the TRX the same invocation wrote.
            if (!coverage.Success)
                Console.Error.WriteLine($"[Lsp] Coverage run failed: {coverage.Message}");
        }

        var outcome = await TestRunService.RunAsync(
            p.ProjectPath, filter, build: true, timeoutSeconds: 600, ct);

        if (outcome.Error is not null)
            Console.Error.WriteLine($"[Lsp] Test run: {outcome.Error}");

        return outcome.Results.Select(r => new TestResultInfo(
            r.FullyQualifiedName, r.Outcome, r.DurationMs, r.ErrorMessage, r.StackTrace)).ToArray();
    }

    public static async Task<TestDebugResult> DebugAsync(TestDebugParams p, CancellationToken ct)
    {
        await using var progress = await ProgressReporter.BeginAsync("Starting test host", ct);

        string? filter = TestRunService.BuildFilter(p.FullyQualifiedNames ?? []);
        var (processId, error) = await TestRunService.StartForDebugAsync(p.ProjectPath, filter, ct);
        return new TestDebugResult(processId, error);
    }

    public static FileCoverageInfo[] Coverage(TestCoverageParams p)
    {
        var data = CoverageService.GetCachedCoverage(out _, out _);
        if (data is null)
            return [];

        return data.Files.Values
            .Select(file => new FileCoverageInfo(
                file.FilePath,
                file.Lines.Values
                    .Select(line => new LineCoverageInfo(line.LineNumber, line.Hits))
                    .OrderBy(line => line.Line)
                    .ToArray()))
            .Where(file => file.Lines.Length > 0)
            .ToArray();
    }
}

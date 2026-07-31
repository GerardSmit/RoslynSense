using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using RoslynMCP.Config;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>
/// workspace/diagnostic: the Problems panel without having to open every file first.
///
/// Scope is deliberately configurable. Sweeping a 200-project solution on every request would
/// be worse than the empty panel it replaces, so the default covers projects the user actually
/// has documents open in. Analyzer results are read from cache only — a sweep must not trigger
/// hundreds of analyzer passes.
/// </summary>
internal static class WorkspaceDiagnosticsHandler
{
    public static async Task<WorkspaceDiagnosticReport> DiagnoseAsync(
        WorkspaceDiagnosticParams p, CancellationToken ct)
    {
        string scope = LspFeatureOptions.WorkspaceDiagnosticsScope;
        if (scope == "off")
            return new WorkspaceDiagnosticReport([]);

        var solution = WorkspaceService.TryGetMostRecentSolution();
        if (solution is null)
            return new WorkspaceDiagnosticReport([]);

        var previous = (p.PreviousResultIds ?? [])
            .ToDictionary(r => r.Uri, r => r.Value, StringComparer.OrdinalIgnoreCase);

        var projects = SelectProjects(solution, scope).ToList();
        if (projects.Count == 0)
            return new WorkspaceDiagnosticReport([]);

        await using var progress = await ProgressReporter.BeginAsync("Analyzing solution", ct);

        var reports = new ConcurrentBag<object>();
        int done = 0;

        await Parallel.ForEachAsync(
            projects,
            new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
            },
            async (project, token) =>
            {
                foreach (var report in await DiagnoseProjectAsync(project, previous, token))
                    reports.Add(report);

                progress.Report(project.Name, (int)(100.0 * Interlocked.Increment(ref done) / projects.Count));
            });

        return new WorkspaceDiagnosticReport(reports.ToArray());
    }

    /// <summary>
    /// Projects in scope. "openProjects" includes the projects that reference an open one too:
    /// an error is most often introduced in the file being edited but surfaces in its consumers.
    /// </summary>
    private static IEnumerable<Project> SelectProjects(Solution solution, string scope)
    {
        if (scope == "solution")
            return solution.Projects;

        var openPaths = OpenDocumentStore.OpenPaths().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (openPaths.Count == 0)
            return [];

        var open = solution.Projects
            .Where(project => project.Documents.Any(d =>
                d.FilePath is { Length: > 0 } path && openPaths.Contains(path)))
            .ToList();

        var ids = open.Select(p => p.Id).ToHashSet();
        var dependents = solution.Projects
            .Where(project => project.ProjectReferences.Any(reference => ids.Contains(reference.ProjectId)));

        return open.Concat(dependents).DistinctBy(p => p.Id);
    }

    private static async Task<List<object>> DiagnoseProjectAsync(
        Project project, IReadOnlyDictionary<string, string> previous, CancellationToken ct)
    {
        var reports = new List<object>();

        foreach (var document in project.Documents)
        {
            ct.ThrowIfCancellationRequested();

            if (document.FilePath is not { Length: > 0 } path)
                continue;

            string uri = LspConverters.PathToUri(path);
            string? version = await AnalyzerDiagnosticCache.GetVersionAsync(document, ct);

            if (version is not null &&
                previous.TryGetValue(uri, out string? previousId) &&
                previousId == version)
            {
                reports.Add(new WorkspaceUnchangedDocumentDiagnosticReport("unchanged", uri, version));
                continue;
            }

            var model = await document.GetSemanticModelAsync(ct);
            if (model is null)
                continue;

            // Cache-only for analyzers: a sweep that ran them would take minutes and pin the CPU.
            var analyzer = AnalyzerDiagnosticCache.TryGet(document, version);
            var items = DiagnosticsHandler
                .Merge(model.GetDiagnostics(cancellationToken: ct), analyzer)
                .Where(d => d.Severity != DiagnosticSeverity.Hidden && d.Location.IsInSource)
                .Select(d => new Protocol.Diagnostic(
                    LspConverters.ToRange(d.Location.GetLineSpan().Span),
                    LspConverters.ToLspSeverity(d.Severity),
                    d.Id,
                    "roslyn-sense",
                    d.GetMessage()))
                .ToArray();

            reports.Add(new WorkspaceFullDocumentDiagnosticReport("full", uri, items)
            {
                ResultId = version,
            });
        }

        return reports;
    }
}

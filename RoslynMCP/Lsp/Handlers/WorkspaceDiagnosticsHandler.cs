using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using RoslynMCP.Config;
using RoslynMCP.Languages;
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
    private static readonly ConditionalWeakTable<Compilation, ConcurrentDictionary<string, bool>>
        s_declaredInterest = new();


    public static async Task<WorkspaceDiagnosticReport> DiagnoseAsync(
        WorkspaceDiagnosticParams p, CancellationToken ct, LanguageSession? languages = null)
    {
        var session = LanguageScope.Of(languages);

        string scope = LspFeatureOptions.WorkspaceDiagnosticsScope;
        if (scope == "off")
            return new WorkspaceDiagnosticReport([]);

        var solution = WorkspaceService.TryGetMostRecentSolution();
        if (solution is null)
            return new WorkspaceDiagnosticReport([]);

        var previous = (p.PreviousResultIds ?? [])
            .ToDictionary(r => r.Uri, r => r.Value, StringComparer.OrdinalIgnoreCase);

        var projects = SelectProjects(solution, scope, session).ToList();
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

                foreach (var report in await DiagnosePackFilesAsync(project, previous, session, token))
                    reports.Add(report);

                if (await DiagnoseBindingRedirectsAsync(project, token) is { } bindings)
                    reports.Add(bindings);

                progress.Report(project.Name, (int)(100.0 * Interlocked.Increment(ref done) / projects.Count));
            });

        return new WorkspaceDiagnosticReport(reports.ToArray());
    }

    /// <summary>
    /// Projects in scope. "openProjects" includes the projects that reference an open one too:
    /// an error is most often introduced in the file being edited but surfaces in its consumers.
    /// </summary>
    private static IEnumerable<Project> SelectProjects(
        Solution solution, string scope, LanguageSession languages)
    {
        if (scope == "solution")
            return solution.Projects;

        var openPaths = OpenDocumentStore.OpenPaths().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (openPaths.Count == 0)
            return [];

        var open = solution.Projects
            .Where(project =>
                project.Documents.Any(d =>
                    d.FilePath is { Length: > 0 } path && openPaths.Contains(path))
                || HasOpenPackFile(project, openPaths, languages))
            .ToList();

        var ids = open.Select(p => p.Id).ToHashSet();
        var dependents = solution.Projects
            .Where(project => project.ProjectReferences.Any(reference => ids.Contains(reference.ProjectId)));

        return open.Concat(dependents).DistinctBy(p => p.Id);
    }

    /// <summary>
    /// Whether a file one of the packs owns is open under this project's directory. A markup file
    /// is not a <see cref="Document"/>, so a window with nothing but <c>.aspx</c> files open would
    /// otherwise select no project at all and report nothing. Containment under the project
    /// directory is the same ownership rule the packs' own file enumeration applies.
    /// </summary>
    private static bool HasOpenPackFile(
        Project project, HashSet<string> openPaths, LanguageSession languages)
    {
        if (languages.Packs.IsEmpty
            || Path.GetDirectoryName(project.FilePath) is not { Length: > 0 } directory)
        {
            return false;
        }

        string prefix = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return openPaths.Any(path =>
            languages.Resolve(path) is not null
            && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The enabled packs' own files in this project. The C# loop cannot reach them — no markup
    /// file is a <see cref="Document"/> — so without this a broken <c>OnClick=</c> stays invisible
    /// until someone opens the page it is on.
    /// </summary>
    /// <summary>
    /// The project's <c>web.config</c> or <c>app.config</c>, checked against what it ships.
    /// </summary>
    /// <remarks>
    /// It has to come from the sweep rather than only from the open buffer: nobody opens a config
    /// file to find out that a redirect went stale, and the redirect that is wrong is the one for a
    /// package they updated without thinking about it.
    /// </remarks>
    private static async Task<object?> DiagnoseBindingRedirectsAsync(Project project, CancellationToken ct)
    {
        if (project.FilePath is not { Length: > 0 } projectPath)
            return null;

        var report = await Services.Packages.BindingRedirectService.AnalyzeAsync(projectPath, ct);
        if (report.ConfigPath is null || report.Findings.Count == 0)
            return null;

        return new WorkspaceFullDocumentDiagnosticReport(
            "full",
            LspConverters.PathToUri(report.ConfigPath),
            BindingRedirectHandler.ToDiagnostics(report));
    }

    private static async Task<IReadOnlyList<object>> DiagnosePackFilesAsync(
        Project project,
        IReadOnlyDictionary<string, string> previous,
        LanguageSession languages,
        CancellationToken ct)
    {
        var reports = new List<object>();

        foreach (var pack in languages.Packs)
        {
            if (pack is not ILanguageWorkspaceDiagnosticContributor contributor
                || !await HasDeclaredInterestAsync(pack, project, ct))
            {
                continue;
            }

            reports.AddRange(await contributor.DiagnoseProjectAsync(project, previous, ct));
        }

        return reports;
    }

    /// <summary>
    /// Whether the pack's declared types resolve in this project's compilation. This is what keeps
    /// a solution-wide sweep free for a pack the solution does not use: a metadata lookup decides
    /// it, before the pack walks a directory or parses anything.
    /// </summary>
    /// <remarks>
    /// Cached against the compilation rather than the project, because a compilation is a snapshot
    /// — the answer cannot go stale for one — and because a sweep asks the same question of every
    /// pack for every project on every request.
    /// </remarks>
    private static async Task<bool> HasDeclaredInterestAsync(
        ILanguagePack pack, Project project, CancellationToken ct)
    {
        if (pack.WellKnownTypeNames.IsDefaultOrEmpty)
            return true;

        var compilation = await project.GetCompilationAsync(ct);
        if (compilation is null)
            return false;

        return s_declaredInterest.GetOrCreateValue(compilation).GetOrAdd(
            pack.Id,
            _ => pack.WellKnownTypeNames.Any(name => compilation.GetTypeByMetadataName(name) is not null));
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

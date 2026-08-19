using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
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
            .GroupBy(r => r.Uri, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);

        // Grouped by project file. A multi-targeted project is several Projects over one document
        // set, so each of its files was reported once per framework, each with that framework's own
        // result id, into one bag in whatever order they finished. The client keeps one id per URI,
        // so it stored whichever arrived last; the other framework's id then mismatched on the next
        // sweep, and that file was re-bound and re-reported for the rest of the session with its
        // diagnostics alternating. Deduplicating afterwards only made the choice nondeterministic
        // and threw away the frameworks it did not pick — a #if NET48 error exists only in one of
        // them. The frameworks are merged instead, under one id built from all of theirs.
        var groups = SelectProjects(solution, scope, session)
            .GroupBy(p => p.FilePath is { Length: > 0 } fp ? Path.GetFullPath(fp) : p.Id.ToString(),
                StringComparer.OrdinalIgnoreCase)
            .Select(g => (IReadOnlyList<Project>)[.. g])
            .ToList();

        if (groups.Count == 0)
            return new WorkspaceDiagnosticReport([]);

        // Deferred: this request is re-sent after anything that could reach another file, and it
        // answers "unchanged" for almost all of them. A notification each time reads as a solution
        // reload — the sweep is only worth announcing when it is actually taking time.
        await using var progress = ProgressReporter.BeginDeferred(
            "Analyzing solution", TimeSpan.FromSeconds(1), ct);

        // Every project that holds each file, and what each calls its version — collected across
        // all groups before a single comparison is made. A file linked into two projects has two
        // versions, and composing its id inside one group meant the id the client stored could
        // never be reproduced by either group alone: both saw a mismatch, both re-bound and
        // re-reported it on every sweep, and the merged report handed back one project's view at a
        // time, so a finding present in only one of them appeared and vanished by turns.
        var versionsByUri = new ConcurrentDictionary<string, ConcurrentBag<(Project Project, Document Document, string Version)>>(
            StringComparer.OrdinalIgnoreCase);

        await Parallel.ForEachAsync(
            groups.SelectMany(g => g),
            new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
            },
            async (project, token) =>
            {
                foreach (var document in project.Documents)
                {
                    token.ThrowIfCancellationRequested();

                    if (document.FilePath is not { Length: > 0 } path)
                        continue;

                    // A document whose version cannot be derived is still reported — with an id
                    // that never matches, so it is sent in full every sweep. Skipping it instead
                    // sends the client no report at all for that URI, and LSP leaves a URI with no
                    // report holding whatever it had, so the file's diagnostics froze for the
                    // session. GetVersionAsync swallows read failures, so this is reachable from a
                    // transient one.
                    string? version = await AnalyzerDiagnosticCache.GetVersionAsync(document, token);

                    // Whether analyzers have run for this version is part of what the id means:
                    // without it the re-pull that follows a background pass answers "unchanged" and
                    // the findings it just computed are never delivered.
                    string stamped = version is null
                        ? $"unversioned:{Guid.NewGuid():N}"
                        : $"{version}:{(AnalyzerDiagnosticCache.IsComputed(document, version) ? "a" : "c")}";

                    versionsByUri
                        .GetOrAdd(LspConverters.PathToUri(path), _ => [])
                        .Add((project, document, stamped));
                }
            });

        var composed = versionsByUri.ToDictionary(
            kv => kv.Key,
            kv => string.Join('|', kv.Value.Select(v => v.Version).OrderBy(v => v, StringComparer.Ordinal)),
            StringComparer.OrdinalIgnoreCase);

        var reports = new ConcurrentBag<object>();
        int done = 0;

        await Parallel.ForEachAsync(
            groups,
            new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
            },
            async (group, token) =>
            {
                // The pack is asked of every framework, and the first that claims the project
                // answers for it. Whether a pack applies is decided by resolving its well-known
                // types against a compilation — System.Web.UI.Control resolves under net48 and not
                // under net8.0 — so asking one arbitrary framework meant a multi-targeted WebForms
                // project silently reported no markup diagnostics at all whenever the framework
                // that happened to be first was the modern one.
                foreach (var framework in group)
                {
                    var packReports = await DiagnosePackFilesAsync(framework, previous, session, token);
                    if (packReports.Count == 0)
                        continue;

                    foreach (var report in packReports)
                        reports.Add(report);
                    break;
                }

                // Framework-independent: this reads the project file and the config beside it, so
                // one framework's worth is the whole answer.
                if (await DiagnoseBindingRedirectsAsync(group[0], token) is { } bindings)
                    reports.Add(bindings);

                progress.Report(group[0].Name, (int)(100.0 * Interlocked.Increment(ref done) / groups.Count));
            });

        // One report per file. A multi-targeted project is several Projects sharing a document
        // set, so each of its documents was reported once per framework — with a different result
        // id each time, into one bag in whatever order they finished. The client kept whichever
        // arrived last, the other framework's id then mismatched on the next sweep, and the file
        // was re-bound and re-reported forever, its diagnostics alternating between the two.
        foreach (var report in await DiagnoseDocumentsAsync(
            composed,
            versionsByUri.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<(Project, Document, string)>)[.. kv.Value],
                StringComparer.OrdinalIgnoreCase),
            previous,
            ct))
        {
            reports.Add(report);
        }

        // One report per URI across the whole response, not merely within a project. A file linked
        // into two projects — <Compile Include="..\Shared\X.cs" /> — is a document in both, so it
        // came back twice with two ids; the protocol allows one report per document, and the client
        // keeps one id, so the other mismatched on every later sweep and that file was re-bound for
        // the rest of the session. Merged rather than dropped, for the same reason frameworks are.
        return new WorkspaceDiagnosticReport([.. MergeByUri(reports)]);
    }

    /// <summary>Collapses reports that name the same document into one.</summary>
    private static IEnumerable<object> MergeByUri(IEnumerable<object> reports)
    {
        var seen = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var others = new List<object>();

        foreach (var report in reports)
        {
            string? uri = report switch
            {
                WorkspaceFullDocumentDiagnosticReport full => full.Uri,
                WorkspaceUnchangedDocumentDiagnosticReport unchanged => unchanged.Uri,
                _ => null,
            };

            if (uri is null)
            {
                others.Add(report);
                continue;
            }

            if (!seen.TryGetValue(uri, out var existing))
            {
                seen[uri] = report;
                continue;
            }

            seen[uri] = Combine(existing, report);
        }

        return seen.Values.Concat(others);
    }

    /// <summary>
    /// Two reports for one document, as one. A full report always wins over an unchanged one — a
    /// project that has something to say about the file outranks one that does not — and two full
    /// reports contribute both their diagnostics under an id built from both.
    /// </summary>
    private static object Combine(object left, object right)
    {
        if (left is not WorkspaceFullDocumentDiagnosticReport a)
            return right is WorkspaceFullDocumentDiagnosticReport ? right : left;

        if (right is not WorkspaceFullDocumentDiagnosticReport b)
            return left;

        string[] ids = [a.ResultId ?? "", b.ResultId ?? ""];

        return new WorkspaceFullDocumentDiagnosticReport(
            "full",
            a.Uri,
            [.. a.Items.Concat(b.Items).DistinctBy(d => (
                d.Range.Start.Line,
                d.Range.Start.Character,
                d.Range.End.Line,
                d.Range.End.Character,
                d.Severity,
                d.Code,
                d.Message))])
        {
            ResultId = string.Join('|', ids.OrderBy(v => v, StringComparer.Ordinal)),
        };
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

    /// <summary>
    /// The enabled packs' own files in this project. The C# loop cannot reach them — no markup
    /// file is a <see cref="Document"/> — so without this a broken <c>OnClick=</c> stays invisible
    /// until someone opens the page it is on.
    /// </summary>
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

    /// <summary>
    /// One report per file, across every project that holds it.
    /// </summary>
    /// <remarks>
    /// Reporting was per project, and that is the wrong unit twice over: a multi-targeted project
    /// is several <see cref="Project"/>s over one document set, and a linked file belongs to
    /// several project files. The client keeps one result id per document, so any file with more
    /// than one version had an id no single project could reproduce — every sweep saw a mismatch,
    /// re-bound it, and handed back one project's view, which made a finding that exists in only
    /// one of them appear and disappear by turns.
    ///
    /// Versions for every file are composed first and compared before any compilation is asked
    /// for. That order is the economy of the whole sweep: the editor re-pulls after anything that
    /// could reach another file, so almost every sweep has nothing to say and must be able to say
    /// so without binding.
    /// </remarks>
    private static async Task<List<object>> DiagnoseDocumentsAsync(
        IReadOnlyDictionary<string, string> composed,
        IReadOnlyDictionary<string, IReadOnlyList<(Project Project, Document Document, string Version)>> byUri,
        IReadOnlyDictionary<string, string> previous,
        CancellationToken ct)
    {
        var reports = new List<object>();
        var stale = new List<string>();

        foreach (var (uri, version) in composed)
        {
            if (previous.TryGetValue(uri, out string? previousId) && previousId == version)
                reports.Add(new WorkspaceUnchangedDocumentDiagnosticReport("unchanged", uri, version));
            else
                stale.Add(uri);
        }

        if (stale.Count == 0)
            return reports;

        // One compilation per project that actually owns something stale, and not before now.
        var needed = stale
            .SelectMany(uri => byUri[uri].Select(v => v.Project))
            .DistinctBy(project => project.Id)
            .ToList();

        // In parallel, as the per-project path it replaced was. A keystroke moves the dependent
        // semantic version of every project that references the edited one, so all of their
        // documents go stale together and each needs a full compiler pass — serializing those made
        // the sweep itself feel like the reload this work exists to remove.
        var byProject = new ConcurrentDictionary<ProjectId, ILookup<SyntaxTree?, Microsoft.CodeAnalysis.Diagnostic>>();

        await Parallel.ForEachAsync(
            needed,
            new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
            },
            async (project, token) =>
            {
                if (await project.GetCompilationAsync(token) is not { } compilation)
                    return;

                byProject[project.Id] = compilation.GetDiagnostics(token)
                    .Where(d => d.Location.IsInSource)
                    .ToLookup(d => d.Location.SourceTree);
            });

        foreach (string uri in stale)
        {
            var items = new List<Protocol.Diagnostic>();

            foreach (var (project, document, _) in byUri[uri])
            {
                ct.ThrowIfCancellationRequested();

                if (!byProject.TryGetValue(project.Id, out var byTree))
                    continue;

                var tree = await document.GetSyntaxTreeAsync(ct);
                string? version = await AnalyzerDiagnosticCache.GetVersionAsync(document, ct);

                // Cache-only for analyzers: a sweep that ran them would take minutes and pin the
                // CPU. On a miss the previous analysis of this same text stands in, so an edit
                // elsewhere does not blank every file's squiggles for a second.
                var analyzer = AnalyzerDiagnosticCache.TryGet(document, version);
                if (!AnalyzerDiagnosticCache.IsComputed(document, version))
                {
                    analyzer = AnalyzerDiagnosticCache.TryGetAnyVersion(document, version);

                    // Only when there is a version to cache against. A document whose version
                    // cannot be derived is reported under an id that never matches, so it is stale
                    // on every sweep — and its analyzer pass is never cached either, so the result
                    // never compares equal to what came before and every pass asks for another
                    // refresh, which runs another sweep. The pass would loop forever without ever
                    // being able to deliver anything.
                    if (version is not null)
                        RecomputeInBackground(document);
                }

                items.AddRange(DiagnosticsHandler
                    .Merge(
                        tree is null ? Enumerable.Empty<Microsoft.CodeAnalysis.Diagnostic>() : byTree[tree],
                        analyzer)
                    .Where(d => d.Severity != DiagnosticSeverity.Hidden && d.Location.IsInSource)
                    .Select(d => new Protocol.Diagnostic(
                        LspConverters.ToRange(d.Location.GetLineSpan().Span),
                        LspConverters.ToLspSeverity(d.Severity),
                        d.Id,
                        "roslyn-sense",
                        d.GetMessage())));

                // The languages inside string literals, and only for a file the editor has open —
                // the editor keeps one set per URI with the last report winning, so omitting them
                // would erase what the document pull published. Detection walks every token and
                // binds each literal's enclosing invocation, which is far too much for every file
                // in a solution.
                if (document.FilePath is { Length: > 0 } path
                    && OpenDocumentStore.IsOpen(path)
                    && await DiagnosticsHandler.EmbeddedDiagnosticsAsync(document, ct) is { Count: > 0 } embedded)
                {
                    items.AddRange(embedded);
                }
            }

            reports.Add(new WorkspaceFullDocumentDiagnosticReport(
                "full",
                uri,
                [.. items.DistinctBy(d => (
                    d.Range.Start.Line,
                    d.Range.Start.Character,
                    d.Range.End.Line,
                    d.Range.End.Character,
                    d.Severity,
                    d.Code,
                    d.Message))])
            {
                ResultId = composed[uri],
            });
        }

        return reports;
    }

    /// <summary>
    /// Brings a closed document's analyzer results up to date, off the request path, and asks the
    /// editor to re-pull once they land.
    /// </summary>
    /// <remarks>
    /// Bounded by the analyzer cache's own in-flight guard, so a sweep over a project full of stale
    /// entries queues one pass per document and not one per sweep. The refresh is the coalescing
    /// one — several of these completing together cost the client a single re-pull.
    /// </remarks>
    private static readonly SemaphoreSlim s_recomputeSlots =
        new(Math.Max(1, Environment.ProcessorCount / 4));

    private static readonly ConcurrentDictionary<DocumentId, byte> s_recomputing = new();

    private static void RecomputeInBackground(Document document)
    {
        // One pass per document at a time. A keystroke moves the project's dependent semantic
        // version, which misses the cache for every closed document in the project and every
        // dependent project at once — so an unguarded call here queued hundreds of full analyzer
        // passes per sweep, on the same thread pool the message loop runs on, and each completion
        // scheduled a refresh that started the next sweep.
        if (!s_recomputing.TryAdd(document.Id, 0))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                // Capped as well as deduplicated: the documents are distinct, so the guard above
                // does not bound how many run together.
                var before = AnalyzerDiagnosticCache.TryGetPrevious(document);

                await s_recomputeSlots.WaitAsync();
                ImmutableArray<Microsoft.CodeAnalysis.Diagnostic> after;
                try
                {
                    after = await AnalyzerDiagnosticCache.GetOrComputeAsync(
                        document, CancellationToken.None);
                }
                finally
                {
                    s_recomputeSlots.Release();
                }

                // Only when the answer actually moved. A keystroke shifts the project's dependent
                // semantic version, so every closed document in it misses the cache and lands
                // here; refreshing unconditionally meant each of those completions asked the editor
                // to re-pull, which ran another sweep, which missed again. Sustained typing never
                // converged — the very symptom this was meant to remove.
                if (!AnalyzerDiagnosticCache.SameFindings(before, after))
                    LspSessionRegistry.ScheduleRefresh(RefreshKind.Diagnostics);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[Lsp] Background analyzers for '{document.Name}' failed: {ex.Message}");
            }
            finally
            {
                s_recomputing.TryRemove(document.Id, out _);
            }
        });
    }

    // Open documents are deliberately NOT skipped any more.
    //
    // They were, because a sweep read analyzers from cache only and would overwrite the document
    // pull's richer answer with the compiler-only subset. That is no longer true: the sweep serves
    // the same cached analyzer results the pull serves, and queues a recompute when they are stale,
    // so the two reports now agree and neither downgrades the other.
    //
    // Skipping them was also unsound in a way no bookkeeping could repair. Whether a file is open
    // is process-wide, but the editors sharing this daemon each hold their own result ids — so a
    // file open in one window was skipped for every window, and only the one that had it open ever
    // discarded its diagnostics. Every attempt to patch that around ended up either losing a
    // client's report permanently or forcing a full project bind on every sweep, forever.
}

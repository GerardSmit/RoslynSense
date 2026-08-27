using System.Collections.Concurrent;
using System.Collections.Immutable;
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
    private static readonly ConcurrentDictionary<(ProjectId, string), (VersionStamp Version, bool Interested)>
        s_declaredInterest = new();


    public static async Task<WorkspaceDiagnosticReport> DiagnoseAsync(
        WorkspaceDiagnosticParams p, CancellationToken ct, LanguageSession? languages = null)
    {
        var session = LanguageScope.Of(languages);

        string scope = LspFeatureOptions.WorkspaceDiagnosticsScope;
        if (scope == "off")
            return new WorkspaceDiagnosticReport([]);

        var solution = WorkspaceService.TryGetSessionSolution();
        if (solution is null)
            return new WorkspaceDiagnosticReport([]);

        // Keyed by the URI as this server spells it, not as the client sent it — every lookup below
        // is against a LspConverters.PathToUri of a real path. See LspConverters.NormalizeUri: the
        // two spellings differ for any file whose name contains a character Uri.AbsoluteUri escapes
        // and VS Code's serialiser does not, a space being the common one, and the mismatch reads
        // as "no previous result" and re-sends that file in full on every sweep, forever.
        var previous = (p.PreviousResultIds ?? [])
            .GroupBy(r => LspConverters.NormalizeUri(r.Uri), StringComparer.OrdinalIgnoreCase)
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
                        : $"{version}:{DiagnosticsHandler.AnalyzerMarker(document, version)}";

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
                if (await DiagnoseBindingRedirectsAsync(group[0], previous, token) is { } bindings)
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
        var merged = MergeByUri(reports).ToList();
        NoteSweepConvergence(merged, previous);
        return new WorkspaceDiagnosticReport([.. merged]);
    }

    /// <summary>Sweeps in a row that re-reported at least one file. A healthy sweep converges: a
    /// couple of passes after an edit, everything answers "unchanged".</summary>
    private static int s_consecutiveChurningSweeps;

    /// <summary>
    /// Detects the sweep failing to converge, and names the churning files while it is happening.
    /// </summary>
    /// <remarks>
    /// Every class of result-id bug this handler's history records — multi-target ids, linked-file
    /// ids, the analyzer cache evicting the fact the id was built from — presented the same way: a
    /// Problems panel that never settles, nothing in the log, and the cause reconstructed by hand
    /// from probe traffic. The signature is cheap to detect at the source: full reports on many
    /// consecutive sweeps, when a converging session answers "unchanged" within a pass or two of
    /// any edit. Ten in a row is past anything a real editing pause produces; the repeat logging
    /// stays keyed and sparse so a long-lived loop cannot flood the log it is meant to explain.
    /// </remarks>
    private static void NoteSweepConvergence(
        IReadOnlyList<object> merged, IReadOnlyDictionary<string, string> previous)
    {
        var full = merged.OfType<WorkspaceFullDocumentDiagnosticReport>().ToList();
        if (full.Count == 0)
        {
            s_consecutiveChurningSweeps = 0;
            return;
        }

        int streak = Interlocked.Increment(ref s_consecutiveChurningSweeps);

        // Which files, not merely how many. A steady count of full reports has two causes that
        // want opposite responses, and the count alone cannot tell them apart: a cold solution
        // working through its first analyzer pass reports a different handful every time and is
        // converging, while a treadmill reports the same handful forever. Carried across passes
        // whether or not this one logs, so the number is about the last pass rather than the last
        // logged one.
        var reported = full.Select(r => r.Uri).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var before = Interlocked.Exchange(ref s_lastFullyReported, reported);
        int repeated = reported.Count(uri => before.Contains(uri));

        if (streak != 10 && streak % 100 != 0)
            return;

        var samples = full.Take(3).Select(report =>
        {
            previous.TryGetValue(report.Uri, out string? was);
            return $"'{Path.GetFileName(LspConverters.UriToPath(report.Uri))}' "
                + $"[{Abbreviate(was)} -> {Abbreviate(report.ResultId)}]";
        });

        Services.ServiceLog.Warn(
            $"The workspace sweep has re-reported files on {streak} consecutive passes "
            + $"({full.Count} full / {merged.Count - full.Count} unchanged this pass, "
            + $"{repeated} of them full on the previous pass too) — result ids "
            + $"are churning instead of converging. E.g. {string.Join(", ", samples)}.",
            key: "sweep-not-converging");
    }

    /// <summary>The files the previous sweep sent in full, to tell a moving front from a treadmill.</summary>
    private static HashSet<string> s_lastFullyReported = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A result id short enough to read in a log line: ids are checksum-semanticVersion:marker per
    /// owning project, joined by '|'.
    /// </summary>
    /// <remarks>
    /// Only the content checksum is elided, and everything after it is kept whole. Eliding the
    /// middle instead dropped the dependent semantic version — the one field that separates the
    /// three reasons an id moves. A moved checksum means the file's own text changed; a moved
    /// semantic version means some other file's declarations did; a moved marker alone means
    /// nothing changed at all and analyzers merely finished. Two of those are a converging session
    /// and one is a bug, and a line that shows only the head and the marker cannot say which.
    /// </remarks>
    private static string Abbreviate(string? resultId)
    {
        if (resultId is null)
            return "(none)";

        return string.Join('|', resultId.Split('|').Select(component =>
        {
            int checksum = component.IndexOfAny(['-', ':']);
            return checksum is > 12 ? $"{component[..12]}…{component[checksum..]}" : component;
        }));
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

        // Through the solution's own path index rather than by enumerating every document of every
        // project. This runs on every sweep — every two seconds while the editor is idle — and it
        // used to materialize a Document wrapper for every file in the solution to answer a
        // question about the handful the user has open, only for the next keystroke's solution fork
        // to throw all of them away.
        //
        // Filtered to ids the solution answers as a Document, because GetDocumentIdsWithFilePath
        // also returns AdditionalDocument and AnalyzerConfigDocument ids, and Project.Documents —
        // what this replaces — contains neither. Without that filter an open solution-root
        // .editorconfig belongs to every project and would silently turn the default open-projects
        // sweep into a solution-wide one.
        var openProjectIds = openPaths
            .SelectMany(path => solution.GetDocumentIdsWithFilePath(path))
            .Where(id => solution.GetDocument(id) is not null)
            .Select(id => id.ProjectId)
            .ToHashSet();

        // Still a scan, but only for the packs: a markup file is not a Document, so no index can
        // answer for it. See HasOpenPackFile.
        var open = solution.Projects
            .Where(project =>
                openProjectIds.Contains(project.Id)
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
    private static async Task<object?> DiagnoseBindingRedirectsAsync(
        Project project, IReadOnlyDictionary<string, string> previous, CancellationToken ct)
    {
        if (project.FilePath is not { Length: > 0 } projectPath)
            return null;

        // Cached, and never waiting on an evaluation. This runs for every full-framework project
        // on every sweep — every two seconds while the editor is idle — and the uncached analysis
        // it used to call walks bin and the lib folder of every packages.config package, then takes
        // the process-wide MSBuild gate to evaluate the project. On a solution mid-load that gate
        // is held for minutes, so the sweep was queueing behind it.
        var report = await Services.Packages.BindingRedirectService.CachedAnalyzeAsync(
            projectPath, waitForEvaluation: false, ct);
        if (report.ConfigPath is null)
            return null;

        // Reported even with nothing to say, and with an id. Both used to be otherwise, and each
        // cost something. No report at all for a config whose redirects were just fixed leaves the
        // client holding the findings it last saw, because LSP treats an absent report as "no news"
        // — so the squiggles outlived the fix. And a full report with no id is one the client can
        // never hand back, so this file was re-sent in full on every sweep for the life of the
        // session, which is also what kept the convergence warning firing forever.
        var diagnostics = BindingRedirectHandler.ToDiagnostics(report);
        string uri = LspConverters.PathToUri(report.ConfigPath);
        string resultId = ResultIdOf(diagnostics);

        // Over the findings themselves rather than over what produced them: nothing here versions
        // bin or the package folders the analysis walks, so an id claiming "unchanged" on any other
        // basis could outlive an answer that had moved. This one cannot — it is a hash of the very
        // payload the unchanged report is standing in for.
        return previous.TryGetValue(uri, out string? previousId) && previousId == resultId
            ? new WorkspaceUnchangedDocumentDiagnosticReport("unchanged", uri, resultId)
            : new WorkspaceFullDocumentDiagnosticReport("full", uri, diagnostics) { ResultId = resultId };
    }

    /// <summary>A result id that stands for exactly this set of diagnostics.</summary>
    private static string ResultIdOf(IReadOnlyList<Protocol.Diagnostic> diagnostics)
    {
        var lines = diagnostics
            .Select(d => string.Join(
                '',
                d.Range.Start.Line,
                d.Range.Start.Character,
                d.Range.End.Line,
                d.Range.End.Character,
                d.Severity,
                d.Code,
                d.Message))
            .OrderBy(line => line, StringComparer.Ordinal);

        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join('', lines)));

        return Convert.ToHexString(hash);
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
    /// Cached per project and validated by the dependent semantic version, not held against a
    /// Compilation instance. It used to be the latter, and that key was correct but useless in
    /// the hot case: every edit forks the solution, so every sweep saw a Compilation it had never
    /// answered for and called <see cref="Project.GetCompilationAsync"/> to re-ask — which forces
    /// the edited project's final compilation, source generators included, once per keystroke
    /// pause. Whether System.Web.UI.Control resolves cannot change when a method body does, and
    /// the dependent semantic version is exactly the stamp that stands still across body edits
    /// and moves on anything that could alter the answer.
    /// </remarks>
    private static async Task<bool> HasDeclaredInterestAsync(
        ILanguagePack pack, Project project, CancellationToken ct)
    {
        if (pack.WellKnownTypeNames.IsDefaultOrEmpty)
            return true;

        var version = await project.GetDependentSemanticVersionAsync(ct);
        var key = (project.Id, pack.Id);
        if (s_declaredInterest.TryGetValue(key, out var cached) && cached.Version == version)
            return cached.Interested;

        var compilation = await project.GetCompilationAsync(ct);
        if (compilation is null)
            return false;

        bool interested = pack.WellKnownTypeNames.Any(
            name => compilation.GetTypeByMetadataName(name) is not null);

        // A reloaded solution mints new ProjectIds, so entries can only accumulate; the ceiling
        // is a runaway guard, far above any real project-times-pack count.
        if (s_declaredInterest.Count > 8192)
            s_declaredInterest.Clear();

        s_declaredInterest[key] = (version, interested);
        return interested;
    }

    /// <summary>One stale document's answer: compiler diagnostics, and — when they were computed
    /// alongside them — the embedded-language findings. Null embedded means "not computed here",
    /// and the caller decides whether the document is open enough to be worth the token walk.</summary>
    private sealed record BoundDocument(
        ImmutableArray<Microsoft.CodeAnalysis.Diagnostic> Compiler,
        IReadOnlyList<Protocol.Diagnostic>? Embedded);

    /// <summary>
    /// The compiler diagnostics of just the stale documents of one project, or <see langword="null"/>
    /// when a document could not be bound this way and the caller should bind the whole compilation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sweep used to answer for one stale file by calling <c>compilation.GetDiagnostics()</c>,
    /// which binds the method bodies of every file in the project. That is the wrong unit, and by a
    /// wide margin, because of what actually goes stale: a result id is
    /// <c>textChecksum:dependentSemanticVersion</c>, and
    /// <see cref="Project.GetDependentSemanticVersionAsync"/> moves on <em>top-level</em> changes
    /// only. Typing inside a method body therefore moves one document's checksum and nothing else —
    /// one stale tree in a project of any size, answered by binding all of it.
    /// </para>
    /// <para>
    /// Measured on a synthetic project whose files genuinely reference each other, binding one
    /// stale tree of 500 took 58 ms against 2787 ms for the whole compilation. Per-tree stayed
    /// ahead at every ratio measured, including when every file was stale (1222 ms against
    /// 2787 ms), so there is no crossover to switch on and no threshold here to tune.
    /// </para>
    /// <para>
    /// The two report the same set. <c>SemanticModel.GetDiagnostics()</c> covers its tree's
    /// declaration diagnostics as well as its method bodies — a duplicate member declared across
    /// two halves of a partial class is reported by both routes — and anything outside source is
    /// dropped by the same <c>IsInSource</c> filter either way. It is also what
    /// <see cref="DiagnosticsHandler"/>'s document pull has always used, so this makes the sweep
    /// agree with the pull rather than merely agree with itself.
    /// </para>
    /// </remarks>
    private static async Task<Dictionary<DocumentId, BoundDocument>?> BindStaleDocumentsAsync(
        Project project,
        IEnumerable<(Project Project, Document Document, string Version)> stale,
        CancellationToken ct)
    {
        var found = new Dictionary<DocumentId, BoundDocument>();
        Compilation? compilation = null;

        foreach (var document in stale.Select(owner => owner.Document).DistinctBy(d => d.Id))
        {
            ct.ThrowIfCancellationRequested();

            // Through the pull path's cache for an open document. The pull has usually just bound
            // this exact version — restricted to the edited member's span when the edit allowed it
            // — and stored the result under the same checksum:dependentSemanticVersion key this
            // sweep composed its result id from, so the common case is a lookup. Binding it again
            // here was a second whole-file bind of the very file being typed in, on every sweep,
            // which for a large file is what kept "Analyzing solution" on screen after every
            // pause. Open documents only: on a miss the cache's compute also detects embedded
            // languages, a walk over every token, and the closed documents a declaration edit
            // makes stale must not pay that per file.
            if (document.FilePath is { Length: > 0 } openPath && OpenDocumentStore.IsOpen(openPath))
            {
                Interlocked.Increment(ref TreesBound);
                var cached = await CompilerDiagnosticCache.GetOrComputeAsync(document, ct);
                found[document.Id] = new BoundDocument(
                    [.. cached.Compiler.Where(d => d.Location.IsInSource)], cached.Embedded);
                continue;
            }

            // A tree the compilation does not hold cannot have a semantic model taken from it.
            // Reachable in principle if a document and its project were read from different
            // snapshots; the whole-compilation path still answers correctly, so this defers to it
            // for the project rather than reporting that document as having no diagnostics.
            if (await document.GetSyntaxTreeAsync(ct) is not { } tree)
                return null;

            // Not before the first closed stale document needs it: a body edit leaves only the
            // edited (open) file stale, and forcing the project's final compilation for a sweep
            // that will not bind against it re-runs its source generators per keystroke pause.
            compilation ??= await project.GetCompilationAsync(ct);
            if (compilation is null || !compilation.ContainsSyntaxTree(tree))
                return null;

            Interlocked.Increment(ref TreesBound);
            found[document.Id] = new BoundDocument(
                [.. compilation.GetSemanticModel(tree)
                    .GetDiagnostics(cancellationToken: ct)
                    .Where(d => d.Location.IsInSource)],
                null);
        }

        return found;
    }

    /// <summary>
    /// How much the sweep answered a document at a time, against how often it fell back to a whole
    /// compilation. A document served from <see cref="CompilerDiagnosticCache"/> counts here too —
    /// the counter pins the unit of work, and the cache hit is the cheapest form of it. Exposed for
    /// tests, which assert on what was <em>not</em> bound — the reports are identical either way,
    /// so counting the work is the only way to pin it.
    /// </summary>
    internal static long TreesBound;

    /// <inheritdoc cref="TreesBound"/>
    internal static long WholeCompilationsBound;

    /// <summary>Zeroes the counters, for a test that needs a cold measurement.</summary>
    internal static void ResetBindCounters()
    {
        Interlocked.Exchange(ref TreesBound, 0);
        Interlocked.Exchange(ref WholeCompilationsBound, 0);
    }

    /// <summary>
    /// Whether the id the client sent back still describes the world this sweep sees.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A single component counts, not just the whole composition. A file that belongs to several
    /// projects — every file of a multi-targeted project, and any file pulled in by a linked
    /// <c>&lt;Compile Include="..\..."/&gt;</c> — is stamped here with one component per owner
    /// joined by <c>'|'</c>, while the document pull answers for the one project it resolved and
    /// returns that project's component alone.
    /// </para>
    /// <para>
    /// The client does not keep the two apart. <c>getAllResultIds</c> overwrites the id stored for
    /// a URI from the workspace sweep with the one from the document pull whenever it is tracking
    /// that document, and sends the result back here as <c>previousResultIds</c> — so a composed
    /// id was being compared against one of its own components and could never be equal. Every
    /// such URI was therefore re-bound on every sweep for as long as it stayed open, which for a
    /// multi-targeted project is every file in it.
    /// </para>
    /// <para>
    /// Accepting a component loses nothing: a component only appears in the composition when its
    /// owning project produced it in this same sweep, so an equal component means that project's
    /// text and semantic version are both unmoved. The full composition is still what gets echoed
    /// back in the report, so the client converges on it as soon as it stops tracking the document.
    /// </para>
    /// </remarks>
    internal static bool Matches(string previousId, string composed)
    {
        if (previousId == composed)
            return true;

        // Only a composition can have components; the common single-owner case ends here.
        if (!composed.Contains('|'))
            return false;

        foreach (var component in composed.AsSpan().Split('|'))
        {
            if (composed.AsSpan()[component].SequenceEqual(previousId))
                return true;
        }

        return false;
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
            if (previous.TryGetValue(uri, out string? previousId) && Matches(previousId, version))
                reports.Add(new WorkspaceUnchangedDocumentDiagnosticReport("unchanged", uri, version));
            else
                stale.Add(uri);
        }

        if (stale.Count == 0)
            return reports;

        // Only the projects that actually own something stale, and inside each, only the documents
        // that went stale — a compilation is forced only when a closed one is among them.
        var staleByProject = stale
            .SelectMany(uri => byUri[uri])
            .GroupBy(owner => owner.Project.Id)
            .ToList();

        // In parallel, as the per-project path it replaced was. A declaration edit moves the
        // dependent semantic version of every project that references the edited one, so all of
        // their documents go stale together — serializing those made the sweep itself feel like the
        // reload this work exists to remove.
        var byProject = new ConcurrentDictionary<ProjectId, Dictionary<DocumentId, BoundDocument>>();
        var wholeBound = new ConcurrentDictionary<ProjectId, bool>();

        await Parallel.ForEachAsync(
            staleByProject,
            new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
            },
            async (group, token) =>
            {
                // Same instance for every entry: they were all read out of one solution snapshot,
                // and a solution returns one Project object per id.
                var project = group.First().Project;

                if (await BindStaleDocumentsAsync(project, group, token) is { } perDocument)
                {
                    byProject[project.Id] = perDocument;

                    // Cache-only here, for the same reason analyzers are: the pass this reads is
                    // the whole-compilation one the tree-at-a-time bind exists to avoid, so a sweep
                    // must never wait on it. On a miss the previous answer stands in and a
                    // background pass brings it current.
                    string? wide = await ProjectWideDiagnosticCache.GetVersionAsync(project, token);
                    if (wide is not null && !ProjectWideDiagnosticCache.IsComputed(project, wide))
                        RefreshProjectWideInBackground(project);

                    return;
                }

                if (await project.GetCompilationAsync(token) is not { } compilation)
                    return;

                Interlocked.Increment(ref WholeCompilationsBound);
                var byTree = compilation.GetDiagnostics(token)
                    .Where(d => d.Location.IsInSource)
                    .ToLookup(d => d.Location.SourceTree);

                var fromWhole = new Dictionary<DocumentId, BoundDocument>();
                foreach (var document in group.Select(owner => owner.Document).DistinctBy(d => d.Id))
                {
                    if (await document.GetSyntaxTreeAsync(token) is { } tree)
                        fromWhole[document.Id] = new BoundDocument([.. byTree[tree]], null);
                }

                byProject[project.Id] = fromWhole;

                // Nothing to add for this project: a whole-compilation pass already carries the
                // family the per-tree path is missing, and merging it again would double it.
                wholeBound[project.Id] = true;
            });

        foreach (string uri in stale)
        {
            var items = new List<Protocol.Diagnostic>();
            var components = new List<string>();

            foreach (var (project, document, stamped) in byUri[uri])
            {
                ct.ThrowIfCancellationRequested();

                string component = stamped;

                if (!byProject.TryGetValue(project.Id, out var byDocument))
                {
                    components.Add(component);
                    continue;
                }

                byDocument.TryGetValue(document.Id, out var bound);
                string? version = await AnalyzerDiagnosticCache.GetVersionAsync(document, ct);

                // Cache-only for analyzers: a sweep that ran them would take minutes and pin the
                // CPU. On a miss the previous analysis of this same text stands in, so an edit
                // elsewhere does not blank every file's squiggles for a second.
                //
                // Gated on the stored findings, not on IsComputed: an analyzed version whose
                // payload was trimmed still stamps "a" into the composed id — that is what keeps
                // eviction invisible to an unmoved file — but a report actually being produced
                // here cannot serve what is gone. It falls back, queues the recompute, and
                // downgrades its own id's marker to "c" below, so the refresh that follows the
                // recompute is not answered "unchanged" and the real findings do land.
                var analyzer = AnalyzerDiagnosticCache.TryGet(document, version);
                if (!AnalyzerDiagnosticCache.HasStoredFindings(document, version))
                {
                    if (component.EndsWith(":a", StringComparison.Ordinal))
                        component = string.Concat(component.AsSpan(0, component.Length - 1), "c");

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

                // Through the pull's own converter, not a copy of it. The editor keeps one
                // diagnostic set per URI and the last report wins, so a sweep that shaped its
                // diagnostics even slightly differently made a file's faded spans depend on which
                // of the two answered for it most recently.
                // What binding this one tree could not see. Empty on the whole-compilation path,
                // which already carries it, and empty until the first background pass lands.
                ImmutableArray<Microsoft.CodeAnalysis.Diagnostic> wide = wholeBound.ContainsKey(project.Id)
                    ? []
                    : ProjectWideDiagnosticCache.TryGetAnyVersion(project, document.FilePath);

                IEnumerable<Microsoft.CodeAnalysis.Diagnostic> compiler = bound?.Compiler ?? [];

                items.AddRange(DiagnosticsHandler.ToProtocol(
                    DiagnosticsHandler.Merge(compiler.Concat(wide), analyzer)));

                // The languages inside string literals, and only for a file the editor has open —
                // the editor keeps one set per URI with the last report winning, so omitting them
                // would erase what the document pull published. Detection walks every token and
                // binds each literal's enclosing invocation, which is far too much for every file
                // in a solution — so when the bind came through the pull's cache, its stored
                // embedded findings are the answer and the walk is not repeated per sweep.
                if (bound?.Embedded is { } embedded)
                {
                    items.AddRange(embedded);
                }
                else if (document.FilePath is { Length: > 0 } path
                    && OpenDocumentStore.IsOpen(path)
                    && await DiagnosticsHandler.EmbeddedDiagnosticsAsync(document, ct) is { Count: > 0 } computed)
                {
                    items.AddRange(computed);
                }

                components.Add(component);
            }

            // Recomposed from the per-owner markers this report was actually served with, rather
            // than echoing composed[uri]: an owner served from fallback carries "c" here while the
            // comparison id keeps saying "a", so the file stays stale until its recompute stores —
            // at which point the sweep re-reports it with the real findings and the ids agree.
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
                ResultId = components.Count == 0
                    ? composed[uri]
                    : string.Join('|', components.OrderBy(v => v, StringComparer.Ordinal)),
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

    private static readonly ConcurrentDictionary<ProjectId, byte> s_refreshingWide = new();

    /// <summary>
    /// Brings a project's whole-compilation-only warnings up to date, off the request path.
    /// </summary>
    /// <remarks>
    /// One project at a time and one pass per project, for the reason the analyzer version of this
    /// is guarded: the sweep sees the same project miss for every stale document it owns, and an
    /// unguarded call would queue a full compilation pass for each of them. Sharing the analyzer
    /// slots as well, because the two are the same resource — a machine running both at once is
    /// running the compiler twice over the same source.
    /// </remarks>
    private static void RefreshProjectWideInBackground(Project project)
    {
        if (!s_refreshingWide.TryAdd(project.Id, 0))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await s_recomputeSlots.WaitAsync();
                bool moved;
                try
                {
                    moved = await ProjectWideDiagnosticCache.RefreshAsync(project, CancellationToken.None);
                }
                finally
                {
                    s_recomputeSlots.Release();
                }

                // Only when the answer moved, or the refresh runs the sweep that starts the pass
                // that asks for the refresh.
                if (moved)
                    LspSessionRegistry.ScheduleRefresh(RefreshKind.Diagnostics, "project-wide-pass-stored");
            }
            catch (Exception ex)
            {
                // The whole exception, not ex.Message: these passes crashed twenty thousand times
                // in one session with two anonymous one-liners, and nothing recorded the frame.
                // The key rate-limits the editor toast; stderr keeps every stack.
                LspLog.Error(
                    $"Project-wide diagnostics for '{project.Name}' failed: {ex}",
                    key: "project-wide-diagnostics-crash");
            }
            finally
            {
                s_refreshingWide.TryRemove(project.Id, out _);
            }
        });
    }

    private static void RecomputeInBackground(Document document)
    {
        // One pass per document at a time. A declaration change moves the project's dependent
        // semantic version, which misses the cache for every closed document in the project and
        // every dependent project at once — so an unguarded call here queued hundreds of full
        // analyzer passes per sweep, on the same thread pool the message loop runs on, and each
        // completion scheduled a refresh that started the next sweep.
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

                // Only when the answer actually moved. A declaration change shifts the project's
                // dependent semantic version, so every closed document in it misses the cache and
                // lands here; refreshing unconditionally meant each of those completions asked the
                // editor to re-pull, which ran another sweep, which missed again. Editing a
                // signature never converged — the very symptom this was meant to remove.
                if (!AnalyzerDiagnosticCache.SameFindings(before, after))
                    LspSessionRegistry.ScheduleRefresh(RefreshKind.Diagnostics, "analyzer-recompute-stored");
            }
            catch (Exception ex)
            {
                // Full stack for the same reason as the project-wide catch above: a message alone
                // ("Object reference not set…") cost a day of guessing at the throwing frame.
                LspLog.Error(
                    $"Background analyzers for '{document.Name}' failed: {ex}",
                    key: "background-analyzers-crash");
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

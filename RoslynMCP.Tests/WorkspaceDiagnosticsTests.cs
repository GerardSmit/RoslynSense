using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Config;
using RoslynMCP.Languages;
using RoslynMCP.Languages.WebForms;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>workspace/diagnostic: the Problems panel without opening every file, scoped so a
/// large solution is not swept on every request.</summary>
[Collection(SharedState.Name)]
public class WorkspaceDiagnosticsTests : IDisposable
{
    private readonly string _scope = LspFeatureOptions.WorkspaceDiagnosticsScope;
    private readonly string _session = $"wsdiag-{Guid.NewGuid():N}";

    public void Dispose()
    {
        LspFeatureOptions.WorkspaceDiagnosticsScope = _scope;
        OpenDocumentStore.CloseSession(_session);
    }

    [Fact]
    public async Task ScopeOffReturnsNothing()
    {
        LspFeatureOptions.WorkspaceDiagnosticsScope = "off";

        var report = await WorkspaceDiagnosticsHandler.DiagnoseAsync(
            new WorkspaceDiagnosticParams(), default);

        Assert.Empty(report.Items);
    }

    [Fact]
    public async Task OpenProjectsScopeReturnsNothingWhenNoDocumentIsOpen()
    {
        LspFeatureOptions.WorkspaceDiagnosticsScope = "openProjects";
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.BrokenProjectFile);

        var report = await WorkspaceDiagnosticsHandler.DiagnoseAsync(
            new WorkspaceDiagnosticParams(), default);

        // Nothing is open, so there is nothing the user is working on to report about.
        Assert.Empty(report.Items);
    }

    /// <summary>
    /// An open document selects its project for the sweep, and the sweep reports the whole project
    /// — the open file included.
    /// </summary>
    /// <remarks>
    /// Open files were once left out, because the sweep read analyzer results from cache only and
    /// would overwrite the document pull's richer answer with the compiler-only subset. That is no
    /// longer how it behaves: the sweep serves the same cached analyzer results the pull serves and
    /// queues a recompute when they are stale, so neither report downgrades the other.
    ///
    /// Skipping them was also unsound. Whether a file is open is process-wide, but the editors
    /// sharing this daemon each hold their own result ids — so a file open in one window was
    /// skipped for every window, and only the window that had it open ever discarded its
    /// diagnostics. The others were told "unchanged" about a file they had nothing for.
    /// </remarks>
    [Fact]
    public async Task OpenProjectsScopeReportsTheWholeProjectIncludingTheOpenDocument()
    {
        LspFeatureOptions.WorkspaceDiagnosticsScope = "openProjects";
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.BrokenProjectFile);

        string path = FixturePaths.BrokenSemanticFile;
        OpenDocumentStore.Open(_session, path, SourceText.From(await File.ReadAllTextAsync(path)), 1);

        var report = await WorkspaceDiagnosticsHandler.DiagnoseAsync(
            new WorkspaceDiagnosticParams(), default);

        Assert.NotEmpty(report.Items);
        var full = report.Items.OfType<WorkspaceFullDocumentDiagnosticReport>().ToList();

        // The closed sibling is what the sweep is for: nobody opened it, so nothing else would
        // ever report it.
        Assert.Contains(full, r => r.Uri.EndsWith("BrokenSyntax.cs", StringComparison.OrdinalIgnoreCase));

        // And the open one is reported too, rather than being silently nobody's job.
        Assert.Contains(full, r => r.Uri.EndsWith("BrokenSemantic.cs", StringComparison.OrdinalIgnoreCase));

        // The fixture is deliberately broken, so the sweep must actually find something.
        Assert.Contains(full, r => r.Items.Length > 0);
    }

    [Fact]
    public async Task UnchangedDocumentsAnswerUnchangedOnASecondSweep()
    {
        LspFeatureOptions.WorkspaceDiagnosticsScope = "openProjects";
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.BrokenProjectFile);

        string path = FixturePaths.BrokenSemanticFile;
        OpenDocumentStore.Open(_session, path, SourceText.From(await File.ReadAllTextAsync(path)), 1);

        var first = await WorkspaceDiagnosticsHandler.DiagnoseAsync(
            new WorkspaceDiagnosticParams(), default);

        var previous = first.Items
            .OfType<WorkspaceFullDocumentDiagnosticReport>()
            .Where(r => r.ResultId is not null)
            .Select(r => new PreviousResultId(r.Uri, r.ResultId!))
            .ToArray();
        Assert.NotEmpty(previous);

        var second = await WorkspaceDiagnosticsHandler.DiagnoseAsync(
            new WorkspaceDiagnosticParams(previous), default);

        // Re-reporting an unchanged world on every sweep is what makes this feature unusable.
        Assert.Contains(second.Items, item => item is WorkspaceUnchangedDocumentDiagnosticReport);
    }

    [Fact]
    public async Task MarkupIsSweptWithoutAnyDocumentBeingOpen()
    {
        LspFeatureOptions.WorkspaceDiagnosticsScope = "solution";
        await UseWebFormsAsync();

        var report = await WorkspaceDiagnosticsHandler.DiagnoseAsync(
            new WorkspaceDiagnosticParams(), default);

        // The point of the sweep: a markup file is not a Roslyn document, so an OnClick= naming a
        // handler that does not exist reached Problems only while the page was open.
        Assert.False(OpenDocumentStore.IsOpen(FixturePaths.EventWiringAspxFile));

        var markup = Assert.Single(
            report.Items.OfType<WorkspaceFullDocumentDiagnosticReport>(),
            r => r.Uri.EndsWith("EventWiring.aspx", StringComparison.OrdinalIgnoreCase));

        var diagnostic = Assert.Single(markup.Items, d => d.Code == "WFC0008");
        Assert.Contains("MissingHandler", diagnostic.Message);
    }

    [Fact]
    public async Task UnchangedMarkupAnswersUnchangedOnASecondSweep()
    {
        LspFeatureOptions.WorkspaceDiagnosticsScope = "solution";
        await UseWebFormsAsync();

        var first = await WorkspaceDiagnosticsHandler.DiagnoseAsync(
            new WorkspaceDiagnosticParams(), default);

        var previous = first.Items
            .OfType<WorkspaceFullDocumentDiagnosticReport>()
            .Where(r => r.ResultId is not null && r.Uri.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase))
            .Select(r => new PreviousResultId(r.Uri, r.ResultId!))
            .ToArray();
        Assert.NotEmpty(previous);

        var second = await WorkspaceDiagnosticsHandler.DiagnoseAsync(
            new WorkspaceDiagnosticParams(previous), default);

        // Re-parsing every page in a site on every sweep is what makes the feature unusable, so
        // the result id has to survive a round trip through the client.
        Assert.Contains(
            second.Items.OfType<WorkspaceUnchangedDocumentDiagnosticReport>(),
            r => r.Uri.EndsWith("EventWiring.aspx", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Editing one page's code-behind re-diagnoses that page and leaves the rest of the site
    /// alone.
    /// </summary>
    /// <remarks>
    /// A markup file's result id has to fold in the code-behind it binds against, or renaming a
    /// handler would leave every page that calls it reported as unchanged against a stale answer.
    /// The whole project's dependent semantic version was what did that folding, and it moves for
    /// any edit anywhere in the project — so every page in the site got a new id and was re-parsed
    /// and re-diagnosed whenever a single <c>.cs</c> moved. Saving an <c>.ascx</c> was enough on
    /// its own, because that regenerates its <c>.designer.cs</c>. Each page now carries its own
    /// code-behind and designer instead.
    /// </remarks>
    [Fact]
    public async Task EditingOneCodeBehindLeavesTheOtherPagesResultIdsAlone()
    {
        LspFeatureOptions.WorkspaceDiagnosticsScope = "solution";
        await UseWebFormsAsync();

        var first = await WorkspaceDiagnosticsHandler.DiagnoseAsync(
            new WorkspaceDiagnosticParams(), default);

        var previous = first.Items
            .OfType<WorkspaceFullDocumentDiagnosticReport>()
            .Where(r => r.ResultId is not null && IsMarkup(r.Uri))
            .Select(r => new PreviousResultId(r.Uri, r.ResultId!))
            .ToArray();

        Assert.True(previous.Length > 1, "This needs a site with more than one page to say anything.");

        string codeBehind = FixturePaths.EventWiringCodeBehindFile;
        string text = await File.ReadAllTextAsync(codeBehind);
        OpenDocumentStore.Open(_session, codeBehind, SourceText.From(text), version: 1);
        try
        {
            // A declaration, so this is a change markup could in principle bind differently
            // against — the case the result id genuinely has to notice.
            OpenDocumentStore.Change(
                codeBehind,
                version: 2,
                _ => SourceText.From(
                    text + "\nnamespace Added { public class Marker { } }\n"));

            var second = await WorkspaceDiagnosticsHandler.DiagnoseAsync(
                new WorkspaceDiagnosticParams(previous), default);

            var unchanged = second.Items
                .OfType<WorkspaceUnchangedDocumentDiagnosticReport>()
                .Select(r => r.Uri)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var id in previous)
            {
                if (id.Uri.EndsWith("EventWiring.aspx", StringComparison.OrdinalIgnoreCase))
                    continue;

                Assert.True(
                    unchanged.Contains(id.Uri),
                    $"Editing EventWiring's code-behind re-diagnosed '{id.Uri}', which does not "
                    + "bind against it. This is the whole-site sweep coming back.");
            }
        }
        finally
        {
            // The buffer edit is undone, and so is everything derived from it while it was open:
            // the markup index cached for this project was built against the edited compilation,
            // and every later test in this collection reads the same process-wide cache.
            OpenDocumentStore.Close(_session, codeBehind);
            ProjectIndexCacheService.InvalidateProject(FixturePaths.AspxProjectFile);
            await WorkspaceService.EvictProjectForTests(FixturePaths.AspxProjectFile);
        }
    }

    private static bool IsMarkup(string uri) =>
        uri.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase)
        || uri.EndsWith(".ascx", StringComparison.OrdinalIgnoreCase)
        || uri.EndsWith(".master", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The sweep asks the registered packs. Calling the handler directly means no host has built a
    /// registry, so this stands in for one.
    /// </summary>
    private static async Task UseWebFormsAsync()
    {
        new LanguageRegistry([new WebFormsLanguage(new MarkdownFormatter())]).Publish();
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.AspxProjectFile);
    }

    /// <summary>
    /// A multi-targeted project reports each of its files once, not once per framework.
    /// </summary>
    /// <remarks>
    /// Several <see cref="Microsoft.CodeAnalysis.Project"/>s share one project file and one
    /// document set, and the editor keeps a single result id per file. Reporting per framework
    /// meant it stored whichever id arrived last — the order is whatever the parallel sweep
    /// happened to finish in — so the other framework's id mismatched on the next sweep, that file
    /// was re-bound and re-reported for the rest of the session, and its diagnostics alternated
    /// between the two. Merging under one id built from all of them is what settles it.
    /// </remarks>
    [Fact]
    public async Task AMultiTargetedProjectReportsEachFileOnce()
    {
        LspFeatureOptions.WorkspaceDiagnosticsScope = "solution";
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.CpmMultiTfmProjectFile);

        var report = await WorkspaceDiagnosticsHandler.DiagnoseAsync(
            new WorkspaceDiagnosticParams(), default);

        var uris = report.Items
            .Select(item => item switch
            {
                WorkspaceFullDocumentDiagnosticReport full => full.Uri,
                WorkspaceUnchangedDocumentDiagnosticReport unchanged => unchanged.Uri,
                _ => null,
            })
            .Where(uri => uri is not null)
            .ToList();

        Assert.NotEmpty(uris);
        Assert.Equal(uris.Count, uris.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// A sweep that changes nothing answers "unchanged" and never touches a compilation.
    /// </summary>
    /// <remarks>
    /// This is the economy the whole sweep rests on: the editor re-pulls after anything that could
    /// reach another file, so almost every sweep has nothing to say. Computing versions first and
    /// binding only what moved is what makes that free — a merge that collected diagnostics before
    /// comparing ids did the most expensive thing a compilation offers and then discarded it.
    /// </remarks>
    [Fact]
    public async Task ASecondSweepOfAMultiTargetedProjectAnswersUnchanged()
    {
        LspFeatureOptions.WorkspaceDiagnosticsScope = "solution";
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.CpmMultiTfmProjectFile);

        var first = await WorkspaceDiagnosticsHandler.DiagnoseAsync(
            new WorkspaceDiagnosticParams(), default);

        var previous = first.Items
            .OfType<WorkspaceFullDocumentDiagnosticReport>()
            .Where(r => r.ResultId is not null)
            .Select(r => new PreviousResultId(r.Uri, r.ResultId!))
            .ToArray();
        Assert.NotEmpty(previous);

        var second = await WorkspaceDiagnosticsHandler.DiagnoseAsync(
            new WorkspaceDiagnosticParams(previous), default);

        // Every id handed back is honoured. One framework's id going unrecognised is what made a
        // multi-targeted project re-bind on every sweep forever.
        foreach (var id in previous)
        {
            Assert.Contains(
                second.Items.OfType<WorkspaceUnchangedDocumentDiagnosticReport>(),
                r => string.Equals(r.Uri, id.Uri, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// The sweep binds the trees that actually went stale, never the compilation around them.
    /// </summary>
    /// <remarks>
    /// A result id is <c>textChecksum:dependentSemanticVersion</c>, and
    /// <c>GetDependentSemanticVersionAsync</c> moves on top-level changes only — so typing inside a
    /// method body leaves one document stale in a project of any size. Answering that with
    /// <c>compilation.GetDiagnostics()</c> bound every file in the project to report on one of
    /// them. Measured on a project of 500 interconnected files, binding the single stale tree took
    /// 58 ms against 2787 ms for the whole compilation, and per-tree stayed ahead even with every
    /// file stale — so there is no ratio at which the old path was the right one.
    ///
    /// Counted rather than asserted on the reports, because the reports are identical either way:
    /// <c>SemanticModel.GetDiagnostics()</c> covers its tree's declaration diagnostics as well as
    /// its method bodies, so the two routes report the same set and only the work differs.
    /// </remarks>
    [Fact]
    public async Task TheSweepBindsStaleTreesRatherThanWholeCompilations()
    {
        LspFeatureOptions.WorkspaceDiagnosticsScope = "solution";
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);

        WorkspaceDiagnosticsHandler.ResetBindCounters();

        var first = await WorkspaceDiagnosticsHandler.DiagnoseAsync(
            new WorkspaceDiagnosticParams(), default);

        // Everything is stale on a first sweep, so it does bind — but a tree at a time.
        Assert.True(WorkspaceDiagnosticsHandler.TreesBound > 0, "the first sweep should bind something");
        Assert.Equal(0L, WorkspaceDiagnosticsHandler.WholeCompilationsBound);

        var previous = first.Items
            .OfType<WorkspaceFullDocumentDiagnosticReport>()
            .Where(r => r.ResultId is not null)
            .Select(r => new PreviousResultId(r.Uri, r.ResultId!))
            .ToArray();
        Assert.NotEmpty(previous);

        WorkspaceDiagnosticsHandler.ResetBindCounters();

        await WorkspaceDiagnosticsHandler.DiagnoseAsync(
            new WorkspaceDiagnosticParams(previous), default);

        // And a sweep that finds nothing stale binds nothing at all — the economy the whole sweep
        // rests on, now measurable rather than merely intended.
        Assert.Equal(0L, WorkspaceDiagnosticsHandler.TreesBound);
        Assert.Equal(0L, WorkspaceDiagnosticsHandler.WholeCompilationsBound);
    }

    /// <summary>
    /// A file owned by several projects is stamped with one id component per owner, joined by
    /// '|'. One component on its own is accepted as unchanged, because that is the only form the
    /// client ever sends back for a file it also pulls diagnostics for.
    /// </summary>
    /// <remarks>
    /// vscode-languageclient's getAllResultIds overwrites the id it stored from the workspace sweep
    /// with the one from the document pull for any URI it is tracking, and the document pull
    /// answers for the one project it resolved. So the composition was compared against one of its
    /// own components and could never be equal, and every file of a multi-targeted project — and
    /// every linked &lt;Compile Include=".."/&gt; — was re-bound on every sweep for as long as it
    /// stayed open.
    ///
    /// Tested on the comparison rather than through the sweep because no fixture reaches it:
    /// composition needs one path to resolve to documents in two projects, and
    /// <see cref="RoslynTestHelpers.OpenProjectAsync"/> loads a single framework of a multi-targeted
    /// project — the CpmSolution MultiTfm fixture declares net10.0 and netstandard2.0 and arrives
    /// here as one. <see cref="ASecondSweepOfAMultiTargetedProjectAnswersUnchanged"/> passes for the
    /// same reason, and passed before this fix too: it feeds the sweep back the ids the sweep just
    /// produced, so it never constructs the mismatch.
    /// </remarks>
    [Theory]
    // The shape the sweep composes for a file in two frameworks, and the shape the pull returns
    // for the same file: a text checksum, a dependent semantic version, and the analyzer marker.
    [InlineData("abc:v1:a", "abc:v1:a|abc:v2:a")]
    [InlineData("abc:v2:a", "abc:v1:a|abc:v2:a")]
    [InlineData("abc:v1:a", "abc:v1:a")]
    public void OneComponentOfAComposedIdIsAcceptedAsUnchanged(string previous, string composed) =>
        Assert.True(WorkspaceDiagnosticsHandler.Matches(previous, composed));

    /// <summary>
    /// And nothing else is. A component that moved has to re-bind — accepting a stale one would
    /// freeze the file's squiggles at whatever the sweep last said about it.
    /// </summary>
    [Theory]
    [InlineData("abc:v3:a", "abc:v1:a|abc:v2:a")]      // no such component
    [InlineData("abc:v1", "abc:v1:a|abc:v2:a")]        // a prefix of one, not one
    [InlineData("v1:a", "abc:v1:a|abc:v2:a")]          // a suffix of one, not one
    [InlineData("abc:v1:c", "abc:v1:a|abc:v2:a")]      // same text, analyzers have since run
    [InlineData("abc:v1:a", "abc:v2:a")]               // uncomposed and different
    public void AnIdThatMatchesNoComponentIsStale(string previous, string composed) =>
        Assert.False(WorkspaceDiagnosticsHandler.Matches(previous, composed));

    /// <summary>
    /// With analyzers switched off, a second sweep of an unmoved solution still answers unchanged.
    /// </summary>
    /// <remarks>
    /// The marker distinguishing "compiler only" from "compiler and analyzers" was composed one way
    /// by the sweep and another by the document pull. With analyzers off nothing is ever stored in
    /// the cache, so the sweep's IsComputed was permanently false and it said "c" forever while the
    /// pull gated on the option first and said "a" — the two could never agree, and the URI was
    /// re-bound every two seconds for the life of the session.
    /// </remarks>
    [Fact]
    public async Task ASecondSweepAnswersUnchangedWithAnalyzersOff()
    {
        bool analyzers = LspFeatureOptions.AnalyzerDiagnostics;
        LspFeatureOptions.AnalyzerDiagnostics = false;
        try
        {
            LspFeatureOptions.WorkspaceDiagnosticsScope = "solution";
            await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);

            var first = await WorkspaceDiagnosticsHandler.DiagnoseAsync(
                new WorkspaceDiagnosticParams(), default);

            var previous = first.Items
                .OfType<WorkspaceFullDocumentDiagnosticReport>()
                .Where(r => r.ResultId is not null)
                .Select(r => new PreviousResultId(r.Uri, r.ResultId!))
                .ToArray();
            Assert.NotEmpty(previous);

            // The marker says "n" on both passes, which is the point: it names the state rather
            // than being derived from a cache that this setting keeps permanently empty.
            Assert.All(previous, id => Assert.EndsWith(":n", id.Value, StringComparison.Ordinal));

            var second = await WorkspaceDiagnosticsHandler.DiagnoseAsync(
                new WorkspaceDiagnosticParams(previous), default);

            foreach (var id in previous)
            {
                Assert.Contains(
                    second.Items.OfType<WorkspaceUnchangedDocumentDiagnosticReport>(),
                    r => string.Equals(r.Uri, id.Uri, StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            LspFeatureOptions.AnalyzerDiagnostics = analyzers;
        }
    }

    [Fact]
    public void CapabilityFollowsTheConfiguredScope()
    {
        LspFeatureOptions.WorkspaceDiagnosticsScope = "off";
        Assert.Equal("off", LspFeatureOptions.WorkspaceDiagnosticsScope);

        LspFeatureOptions.WorkspaceDiagnosticsScope = "solution";
        Assert.NotEqual("off", LspFeatureOptions.WorkspaceDiagnosticsScope);
    }

    /// <summary>
    /// One keystroke binds one tree, not the project it lives in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The claim the whole change rests on, and the only test here that can fail if the sweep goes
    /// back to binding whole compilations: the counters are the evidence, because the reports are
    /// identical either way. A body edit moves one document's text checksum and no project's
    /// declaration version, so exactly one document is stale — and this fixture has thirteen, which
    /// is enough for "one" and "all of them" to be different answers.
    /// </para>
    /// <para>
    /// Scoped to the open project rather than the solution, which is what keeps it affordable: the
    /// suite shares one workspace, so a solution-scoped settle loop sweeps every project every
    /// other test has loaded, several times over.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task EditingOneFileBindsThatOneTreeAndNoOther()
    {
        LspFeatureOptions.WorkspaceDiagnosticsScope = "openProjects";
        string path = FixturePaths.AspxPageHelperFile;
        string text = await File.ReadAllTextAsync(path);

        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.AspxProjectFile);
        OpenDocumentStore.Open(_session, path, SourceText.From(text), version: 1);

        // Settle first. Analyzer passes finish in the background and the marker they move is part
        // of the result id, so a document can go stale for reasons that have nothing to do with the
        // edit under test. Sweep until one changes nothing, and measure from there.
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool settled = false;

        for (int i = 0; i < 15 && !settled; i++)
        {
            var report = await WorkspaceDiagnosticsHandler.DiagnoseAsync(
                new WorkspaceDiagnosticParams([.. ids.Select(kv => new PreviousResultId(kv.Key, kv.Value))]),
                default);

            var full = report.Items
                .OfType<WorkspaceFullDocumentDiagnosticReport>()
                .Where(r => r.ResultId is not null)
                .ToList();

            foreach (var r in full)
                ids[r.Uri] = r.ResultId!;

            settled = full.Count == 0;
            if (!settled)
                await Task.Delay(100);
        }

        Assert.True(settled, "the sweep never reached a state where nothing was stale");
        Assert.True(ids.Count > 5, $"the fixture needs several documents to tell 'one' from 'all'; saw {ids.Count}");

        // A comment, deliberately: it moves this file's text and no declaration anywhere, which is
        // the shape of almost every keystroke and the one the old sweep paid a whole compilation for.
        OpenDocumentStore.Change(path, version: 2, _ => SourceText.From(text + Environment.NewLine + "// keystroke" + Environment.NewLine));

        WorkspaceDiagnosticsHandler.ResetBindCounters();

        await WorkspaceDiagnosticsHandler.DiagnoseAsync(
            new WorkspaceDiagnosticParams([.. ids.Select(kv => new PreviousResultId(kv.Key, kv.Value))]),
            default);

        Assert.Equal(0L, WorkspaceDiagnosticsHandler.WholeCompilationsBound);
        Assert.True(
            WorkspaceDiagnosticsHandler.TreesBound is > 0 and <= 2,
            $"one file was edited and {WorkspaceDiagnosticsHandler.TreesBound} trees were bound "
            + $"across {ids.Count} documents; a multi-targeted project binds it once per framework.");
    }

    /// <summary>
    /// The warnings a tree-at-a-time bind cannot see still reach the Problems panel.
    /// </summary>
    /// <remarks>
    /// The fixture carries a real one: <c>OutlineShowcase.StaticChanged</c> is declared and never
    /// raised, which the build reports as CS0067. Binding that one tree cannot produce it — whether
    /// an event is used is a fact about the whole project — so a sweep that only bound stale trees
    /// dropped it, and the file the user never opened lost a warning the build still shows.
    /// </remarks>
    [Fact]
    public async Task TheSweepRecoversWarningsOnlyAWholeCompilationCanSee()
    {
        LspFeatureOptions.WorkspaceDiagnosticsScope = "solution";
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);

        ProjectWideDiagnosticCache.Clear();
        WorkspaceDiagnosticsHandler.ResetBindCounters();

        // Nothing on the first sweep: reading this cache is free, filling it is not, so the sweep
        // schedules the pass and answers with what it already had.
        var first = await WorkspaceDiagnosticsHandler.DiagnoseAsync(
            new WorkspaceDiagnosticParams(), default);
        Assert.DoesNotContain("CS0067", CodesFor(first, "OutlineShowcase"));

        // Polling the report itself, because the sweep schedules a pass for every project in scope
        // and this asserts about one file in one of them.
        List<string> codes = [];
        for (int i = 0; i < 100 && !codes.Contains("CS0067"); i++)
        {
            await Task.Delay(100);
            codes = CodesFor(
                await WorkspaceDiagnosticsHandler.DiagnoseAsync(new WorkspaceDiagnosticParams(), default),
                "OutlineShowcase");
        }

        Assert.Contains("CS0067", codes);

        // And the recovery did not cost the economy it was added to preserve.
        Assert.Equal(0L, WorkspaceDiagnosticsHandler.WholeCompilationsBound);
    }

    private static List<string> CodesFor(WorkspaceDiagnosticReport report, string file) =>
    [
        .. report.Items
            .OfType<WorkspaceFullDocumentDiagnosticReport>()
            .Where(r => r.Uri.Contains(file, StringComparison.OrdinalIgnoreCase))
            .SelectMany(r => r.Items.Select(d => d.Code ?? string.Empty))
    ];

    /// <summary>
    /// The full pass runs once per declaration version, not once per sweep.
    /// </summary>
    /// <remarks>
    /// This is what makes patching the family back in affordable. Body edits leave the version
    /// where it was, so sustained typing reads the cached answer and never queues the pass —
    /// getting it wrong would put a whole-compilation bind behind every sweep, which is the cost
    /// binding stale trees exists to remove.
    /// </remarks>
    [Fact]
    public async Task TheProjectWidePassRunsOncePerDeclarationVersion()
    {
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);

        var solution = WorkspaceService.TryGetMostRecentSolution();
        Assert.NotNull(solution);

        var project = solution.Projects.First(
            p => p.FilePath is { } f && f.EndsWith("SampleProject.csproj", StringComparison.OrdinalIgnoreCase));

        ProjectWideDiagnosticCache.Clear();

        Assert.True(await ProjectWideDiagnosticCache.RefreshAsync(project, default),
            "the first pass has nothing to compare against, so it counts as movement");

        // Same version, so nothing to run and nothing to tell the editor about.
        Assert.False(await ProjectWideDiagnosticCache.RefreshAsync(project, default));
    }

    /// <summary>
    /// Pins the id list the sweep patches back in, by diffing the two passes it sits between.
    /// </summary>
    /// <remarks>
    /// The list is written out rather than derived, because deriving it would mean binding every
    /// tree a second time to subtract — the pass the sweep exists to avoid. This is what makes the
    /// shortcut safe to keep: a Roslyn version that adds a fifth compilation-only warning fails
    /// here, rather than quietly dropping it from every closed file.
    /// </remarks>
    [Fact]
    public void TheCompilationOnlyIdsAreExactlyWhatBindingOneTreeMisses()
    {
        // Built to provoke both passes at once: the unused-member family alongside diagnostics that
        // are ordinarily the awkward ones — unreachable code, an obsolete call, a null dereference,
        // and a second file so cross-tree binding is in play.
        const string source = """
            using System;

            class Probe
            {
                private int _neverUsed;
                private int _assignedNeverRead;
                private int _readNeverAssigned;
                private event EventHandler? _neverRaised;

                int Read() => _readNeverAssigned;

                void Body()
                {
                    _assignedNeverRead = 3;
                    int unusedLocal = 1;
                    object o = null;
                    o.ToString();
                }

                [Obsolete] void Old() { }
                void CallsOld() => Old();
                void Throws() { throw new Exception(); Console.WriteLine("unreachable"); }
            }
            """;

        // A partial type split across files, because Roslyn compiles a field initializer as part of
        // the constructor — which is declared in the other file. If binding one tree at a time were
        // going to lose an ordinary error anywhere, it would be here.
        const string declaresConstructor = """
            partial class Split
            {
                public Split() { }
                private int _fromA = 1;
                private int _unusedInA;
            }
            """;

        const string declaresInitializer = """
            partial class Split
            {
                private int _fromB = "not an int";
                private int _unusedInB;
            }
            """;

        var first = CSharpSyntaxTree.ParseText(source, path: "probe.cs");
        var second = CSharpSyntaxTree.ParseText("class Other { void Use() => _ = new Probe(); }", path: "other.cs");
        var third = CSharpSyntaxTree.ParseText(declaresConstructor, path: "split-ctor.cs");
        var fourth = CSharpSyntaxTree.ParseText(declaresInitializer, path: "split-init.cs");

        var compilation = CSharpCompilation.Create(
            "ProbeAssembly",
            [first, second, third, fourth],
            AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        static string Key(Microsoft.CodeAnalysis.Diagnostic d) =>
            $"{d.Id}@{d.Location.SourceTree!.FilePath}:{d.Location.SourceSpan.Start}";

        var whole = compilation.GetDiagnostics()
            .Where(d => d.Location.IsInSource)
            .ToList();

        var perTree = new[] { first, second, third, fourth }
            .SelectMany(t => compilation.GetSemanticModel(t).GetDiagnostics().Where(d => d.Location.IsInSource))
            .Select(Key)
            .ToHashSet(StringComparer.Ordinal);

        var missed = whole.Where(d => !perTree.Contains(Key(d))).Select(d => d.Id).ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(missed);
        Assert.Empty(missed.Except(ProjectWideDiagnosticCache.CompilationOnlyIds));

        // Named rather than left to the set difference: an error in a field initializer is the one
        // an unsound per-tree bind would drop, and dropping an error is worse than dropping a
        // warning. It has to survive in the file that wrote it, not the file that owns the
        // constructor it is compiled into.
        Assert.Contains(perTree, k => k.StartsWith("CS0029@split-init.cs", StringComparison.Ordinal));

        // The other direction too: binding one tree adds nothing of its own, so patching the
        // difference back in is all the sweep has to do to match a whole-compilation answer.
        Assert.Empty(perTree.Except(whole.Select(Key)));
    }
}

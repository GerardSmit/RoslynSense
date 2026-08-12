using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Config;
using RoslynMCP.Languages.Resources.Core;
using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;
using Xunit.Abstractions;

namespace RoslynMCP.Tests;

/// <summary>
/// The same incrementality claims as <see cref="IncrementalInvalidationTests"/>, against a real
/// DNN 7 site instead of a fixture.
/// </summary>
/// <remarks>
/// <para>
/// The fixtures hold a handful of files each, which is enough to prove that exactly one was
/// re-parsed but not enough for anyone to feel the difference — at five files, rebuilding
/// everything is also instant. That is precisely how these regressions survived: every test
/// passed, and the cost only became visible on a site with hundreds of controls, where saving one
/// of them stalled the editor for seconds. This runs the same operations over
/// <see cref="DnnCorpus"/>: 238 markup files, 190 <c>.resx</c>, and a project of about 1,250
/// <c>.cs</c>.
/// </para>
/// <para>
/// Assertions are structural — how many files were re-read — with the elapsed times reported
/// rather than asserted, except for one deliberately loose ratio. A wall-clock budget on a machine
/// that is also running the rest of a test suite is a flake generator, and "one file was re-parsed
/// instead of 238" is the property that makes it fast, stated directly.
/// </para>
/// </remarks>
[Collection(SharedState.Name)]
public class DnnIncrementalOuterTests
{
    private readonly ITestOutputHelper _output;

    public DnnIncrementalOuterTests(ITestOutputHelper output) => _output = output;

    /// <summary>The DNN website: the WebForms half, where the markup and the local resources
    /// are.</summary>
    private static string WebsiteDirectory => Path.Combine(DnnCorpus.Directory, "Website");

    /// <summary>The platform core, and the largest single C# project in the checkout.</summary>
    private static string LibraryProjectFile => Path.Combine(
        DnnCorpus.Directory, "DNN Platform", "Library", "DotNetNuke.Library.csproj");

    /// <summary>
    /// A project whose directory is the site, for the markup index to walk.
    /// </summary>
    /// <remarks>
    /// The DNN website is an old-style Web Site project — there is no <c>.csproj</c> to load, which
    /// is exactly the shape of site this pack exists to serve. The markup index only reads the
    /// project for its directory and a compilation to resolve control types against, so an
    /// in-memory project standing at the site root drives the real builder over the real files.
    /// The compilation resolves nothing, which costs some control types their identity and costs
    /// this test nothing: how many files were re-parsed does not depend on what they parsed into.
    /// </remarks>
    private static Project SiteProject()
    {
        var workspace = new AdhocWorkspace();
        return workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Default,
            name: "DnnWebsite",
            assemblyName: "DnnWebsite",
            language: LanguageNames.CSharp,
            filePath: Path.Combine(WebsiteDirectory, "DnnWebsite.csproj")));
    }

    private static PreviousResultId[] Ids(WorkspaceDiagnosticReport report) =>
    [
        .. report.Items
            .OfType<WorkspaceFullDocumentDiagnosticReport>()
            .Where(r => r.ResultId is not null)
            .Select(r => new PreviousResultId(r.Uri, r.ResultId!))
    ];

    /// <summary>
    /// The ids the client would be holding after <paramref name="report"/>: the new one for every
    /// document that reported full, and the one it already had for everything else.
    /// </summary>
    private static PreviousResultId[] Merge(PreviousResultId[] held, WorkspaceDiagnosticReport report)
    {
        var byUri = held.ToDictionary(id => id.Uri, id => id.Value, StringComparer.OrdinalIgnoreCase);

        foreach (var id in Ids(report))
            byUri[id.Uri] = id.Value;

        return [.. byUri.Select(pair => new PreviousResultId(pair.Key, pair.Value))];
    }

    private static Dictionary<string, AspxParseResult> ByPath(AspxProjectIndex index) =>
        index.Files.ToDictionary(f => Path.GetFullPath(f.FilePath), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Saving one control on a site with 238 of them re-parses one file.
    /// </summary>
    /// <remarks>
    /// This is the regression in its original form. Every markup edit marked the index dirty, and
    /// rebuilding it re-walked the site and re-parsed every <c>.ascx</c>, <c>.aspx</c> and
    /// <c>.master</c> under it — which is the "Analyzing solution" pause the user reported, once
    /// per save.
    /// </remarks>
    [DnnOuterFact]
    public async Task SavingOneControlOnARealSiteReparsesOnlyThatControl()
    {
        var project = SiteProject();

        var cold = Stopwatch.StartNew();
        var first = await AspxSourceMappingService.BuildProjectIndexAsync(project);
        cold.Stop();

        var before = ByPath(first);
        _output.WriteLine($"Cold index: {before.Count} markup files in {cold.ElapsedMilliseconds} ms.");

        Assert.True(
            before.Count > 200,
            $"Expected a site's worth of markup and found {before.Count}; the corpus is not what "
            + "this test was written against.");

        // A control in the middle of the site rather than the first one the walk happened to
        // return, so nothing can pass by ordering.
        string edited = before.Keys.Order(StringComparer.OrdinalIgnoreCase).ElementAt(before.Count / 2);

        var warm = Stopwatch.StartNew();
        var second = await AspxSourceMappingService.UpdateProjectIndexAsync(project, first, [edited]);
        warm.Stop();

        var after = ByPath(second);
        _output.WriteLine(
            $"One save ({Path.GetFileName(edited)}): {warm.ElapsedMilliseconds} ms "
            + $"({(cold.ElapsedMilliseconds == 0 ? 0 : 100.0 * warm.ElapsedMilliseconds / cold.ElapsedMilliseconds):F1}% of cold).");

        Assert.Equal(before.Keys.Order(), after.Keys.Order());

        int reparsed = before.Count(entry => !ReferenceEquals(entry.Value, after[entry.Key]));
        Assert.True(
            reparsed == 1,
            $"One control was saved and {reparsed} of {before.Count} files were re-parsed.");

        // Loose on purpose: the property that matters is the count above, and the only thing this
        // adds is that the count is not being satisfied by some other cost taking its place.
        Assert.True(
            warm.ElapsedMilliseconds <= Math.Max(50, cold.ElapsedMilliseconds / 4),
            $"Re-parsing one file of {before.Count} took {warm.ElapsedMilliseconds} ms against "
            + $"{cold.ElapsedMilliseconds} ms to parse all of them.");
    }

    /// <summary>
    /// Grouping 190 real <c>.resx</c> costs one directory when one of them is edited.
    /// </summary>
    /// <remarks>
    /// DNN is the reason the directory is the unit: its localization is <c>App_LocalResources</c>
    /// folders, one per page or control, each holding a neutral file and its translations. A
    /// per-file invalidation would split families that are only meaningful together; a global one
    /// would drop every folder on the site for one edit.
    /// </remarks>
    [DnnOuterFact]
    public void EditingOneResourceFileOnARealSiteDropsOnlyItsOwnFolder()
    {
        var build = Stopwatch.StartNew();
        var catalog = ResourceCatalogService.Get(WebsiteDirectory);
        build.Stop();

        _output.WriteLine(
            $"Catalog: {catalog.Families.Length} families over "
            + $"{catalog.ByDirectory.Count} directories in {build.ElapsedMilliseconds} ms.");

        Assert.True(
            catalog.ByDirectory.Count > 20,
            $"Expected a site's worth of resource folders and found {catalog.ByDirectory.Count}.");

        // Two families in different folders, both read, so both have something to lose. The
        // catalog's own (unloaded) families are what get asked again afterwards: handing a loaded
        // one back to Load returns it untouched by definition, which would pass this test against
        // any invalidation policy at all.
        var directories = catalog.ByDirectory.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();
        var edited = catalog.ByDirectory[directories[0]][0];
        var untouched = catalog.ByDirectory[directories[^1]][0];

        var editedBefore = ResourceCatalogService.Load(edited);
        var untouchedBefore = ResourceCatalogService.Load(untouched);
        Assert.True(editedBefore.KeysLoaded && untouchedBefore.KeysLoaded);

        ResourceCatalogService.InvalidateContent(editedBefore.Files[0].FilePath);

        Assert.False(
            ReferenceEquals(editedBefore, ResourceCatalogService.Load(edited)),
            "The edited folder kept its key tables and would serve stale strings.");

        Assert.True(
            ReferenceEquals(untouchedBefore, ResourceCatalogService.Load(untouched)),
            $"Editing a .resx in '{Path.GetFileName(directories[0])}' dropped the key tables of "
            + $"'{Path.GetFileName(directories[^1])}' as well.");
    }

    /// <summary>
    /// Opening a second file does not re-report the diagnostics of the first 1,250.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The complaint this whole effort started from, at the scale it was reported at: every time a
    /// file was opened the server said "Analyzing solution", and every warning and error in every
    /// other file vanished and came back. Two sweeps of the same unchanged project must agree that
    /// nothing changed, and opening an unrelated file must not change that answer — an open buffer
    /// that matches the file on disk is not an edit.
    /// </para>
    /// <para>
    /// Asserted as a proportion rather than "all of them": a handful of documents legitimately
    /// report full on the second pass — anything whose analyzer results were still being computed
    /// when the first sweep answered. What cannot happen is the whole project going full.
    /// </para>
    /// <para>
    /// The two elapsed times reported here mean different things and only one of them is a budget.
    /// The settled sweep — the one the editor repeats while nobody is typing — is tens of
    /// milliseconds over 1,251 documents, and that is asserted. The sweep straight after a file is
    /// opened takes seconds, and is not: it is that one file's first analysis, which has to bind a
    /// compilation over the whole project before it can say anything about any of it. That is one
    /// file's diagnostics arriving, not the site's being recomputed, and the unchanged count beside
    /// it is what says so.
    /// </para>
    /// </remarks>
    [DnnOuterFact]
    public async Task OpeningAFileDoesNotReanalyseTheRestOfARealProject()
    {
        string previousScope = LspFeatureOptions.WorkspaceDiagnosticsScope;
        string session = $"dnn-sweep-{Guid.NewGuid():N}";
        LspFeatureOptions.WorkspaceDiagnosticsScope = "openProjects";

        try
        {
            var project = await RoslynTestHelpers.OpenProjectAsync(LibraryProjectFile);

            var paths = project.Documents
                .Select(d => d.FilePath)
                .OfType<string>()
                .Where(File.Exists)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();

            Assert.True(paths.Count > 500, $"Expected the platform core and found {paths.Count} files.");

            // One file open, which is what selects the project for this scope.
            string anchor = paths[0];
            OpenDocumentStore.Open(session, anchor, SourceText.From(await File.ReadAllTextAsync(anchor)), 1);

            var cold = Stopwatch.StartNew();
            var first = await WorkspaceDiagnosticsHandler.DiagnoseAsync(new WorkspaceDiagnosticParams(), default);
            cold.Stop();

            var ids = Ids(first);
            _output.WriteLine($"Cold sweep: {first.Items.Count()} reports in {cold.ElapsedMilliseconds} ms.");
            Assert.True(ids.Length > 500, $"The cold sweep produced only {ids.Length} result ids.");

            // A settling pass before anything is measured. The cold sweep answers before the
            // analyzers it queued have finished, and a document's id says whether they have — so
            // the sweep straight after a cold one legitimately re-reports the ones that completed
            // in between. That is the analysis arriving, not a re-analysis, and timing it would
            // measure the tail of the cold pass under the name of the warm one.
            var settle = await WorkspaceDiagnosticsHandler.DiagnoseAsync(
                new WorkspaceDiagnosticParams(ids), default);
            ids = Merge(ids, settle);

            var steady = Stopwatch.StartNew();
            var quiet = await WorkspaceDiagnosticsHandler.DiagnoseAsync(
                new WorkspaceDiagnosticParams(ids), default);
            steady.Stop();
            ids = Merge(ids, quiet);
            _output.WriteLine($"Steady sweep: {steady.ElapsedMilliseconds} ms.");

            // The sweep the editor actually repeats: nothing has changed, and answering so must
            // cost almost nothing. Generous next to the tens of milliseconds this measures, tight
            // enough that the seconds-long stall being guarded against cannot pass.
            Assert.True(
                steady.ElapsedMilliseconds < 1500,
                $"A sweep of {ids.Length} unchanged documents took {steady.ElapsedMilliseconds} ms.");

            // The gesture: a second file opened, its buffer identical to what is on disk.
            string opened = paths[paths.Count / 2];
            OpenDocumentStore.Open(session, opened, SourceText.From(await File.ReadAllTextAsync(opened)), 1);

            var warm = Stopwatch.StartNew();
            var second = await WorkspaceDiagnosticsHandler.DiagnoseAsync(
                new WorkspaceDiagnosticParams(ids), default);
            warm.Stop();

            int reported = second.Items.Count();
            int unchanged = second.Items.OfType<WorkspaceUnchangedDocumentDiagnosticReport>().Count();
            _output.WriteLine(
                $"After opening {Path.GetFileName(opened)}: {unchanged}/{reported} "
                + $"unchanged in {warm.ElapsedMilliseconds} ms.");

            Assert.True(
                unchanged >= reported * 0.9,
                $"Opening one file made {reported - unchanged} of {reported} "
                + "documents re-report their diagnostics; this is the whole-solution re-analysis "
                + "the user reported.");
        }
        finally
        {
            OpenDocumentStore.CloseSession(session);
            LspFeatureOptions.WorkspaceDiagnosticsScope = previousScope;
        }
    }

    /// <summary>
    /// Typing in one file of a 1,250-file project re-reads that file.
    /// </summary>
    /// <remarks>
    /// The wrapper list is project-wide and any <c>.cs</c> write marks it stale, so what this
    /// measures is the cost of re-deriving it: it used to be the full text of every document in
    /// the project and in each project it references, allocated as a string and searched, on every
    /// save. This is also the only test in the suite that loads a real .NET Framework project of
    /// this size, so it doubles as a check that one can be opened at all.
    /// </remarks>
    [DnnOuterFact]
    public async Task TypingInOneFileOfARealProjectRereadsOnlyThatFile()
    {
        var load = Stopwatch.StartNew();
        var project = await RoslynTestHelpers.OpenProjectAsync(LibraryProjectFile);
        load.Stop();

        int documents = project.Documents.Count();
        _output.WriteLine($"Loaded {documents} documents in {load.ElapsedMilliseconds} ms.");

        Assert.True(
            documents > 500,
            $"Expected the platform core and found {documents} documents; either the corpus is "
            + "not what this test was written against or the project did not load.");

        var warmup = Stopwatch.StartNew();
        await ProjectIndexCacheService.GetFindControlWrappersAsync(project);
        warmup.Stop();
        _output.WriteLine($"Cold wrapper scan: {warmup.ElapsedMilliseconds} ms.");

        string path = project.Documents
            .Select(d => d.FilePath)
            .OfType<string>()
            .Order(StringComparer.OrdinalIgnoreCase)
            .ElementAt(documents / 2);

        string session = $"dnn-outer-{Guid.NewGuid():N}";
        string text = await File.ReadAllTextAsync(path);
        OpenDocumentStore.Open(session, path, SourceText.From(text), version: 1);
        try
        {
            OpenDocumentStore.Change(
                path, version: 2, _ => SourceText.From(text + "\n// keystroke\n"));

            var edited = await RoslynTestHelpers.OpenProjectAsync(LibraryProjectFile);
            Assert.True(
                ProjectIndexCacheService.NotifyFileChangedForTests(LibraryProjectFile, path, false),
                "The project has no cache entry, so the assertions below would be vacuous.");

            long before = AspxSourceMappingService.AccessorScanCount;
            var warm = Stopwatch.StartNew();
            await ProjectIndexCacheService.GetFindControlWrappersAsync(edited);
            warm.Stop();
            long scanned = AspxSourceMappingService.AccessorScanCount - before;

            _output.WriteLine(
                $"One keystroke ({Path.GetFileName(path)}): {scanned} documents re-read in "
                + $"{warm.ElapsedMilliseconds} ms.");

            Assert.True(
                scanned == 1,
                $"One file was edited and {scanned} of {documents} were re-read.");
        }
        finally
        {
            OpenDocumentStore.Close(session, path);
        }
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Proto.Core;
using RoslynMCP.Languages.Resources.Core;
using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// One test per file type for the question every cached index has to answer: when this kind of
/// file changes, how much is thrown away?
/// </summary>
/// <remarks>
/// <para>
/// The bug these exist for was always the same shape and never visible in a result. Editing one
/// <c>.ascx</c> marked the whole markup index dirty, so the next request re-walked the site and
/// re-parsed every page; saving any <c>.cs</c> marked the wrapper list dirty, so the next request
/// read the entire text of every document in the project and its references. Both returned exactly
/// the right answer, which is why neither showed up in a correctness test — they only showed up as
/// a site that went quiet for several seconds every time somebody pressed Ctrl+S.
/// </para>
/// <para>
/// So these assert on what was <em>reused</em> rather than on what was returned: parse results
/// carried over from the previous index are the same objects, and the scan counters say how many
/// documents were actually read. Asserting on the returned index would pass just as happily
/// against the version that rebuilt everything.
/// </para>
/// <para>
/// Invalidation is driven through <see cref="ProjectIndexCacheService.NotifyFileChangedForTests"/>
/// rather than by writing files, because the policy under test is which flags an extension sets —
/// not whether a <see cref="FileSystemWatcher"/> delivers. <see cref="WatchedFilesTests"/> covers
/// the LSP side of the same story, where the events come from the client.
/// <see cref="DnnIncrementalOuterTests"/> checks that these policies still hold on a real site.
/// </para>
/// </remarks>
[Collection(SharedState.Name)]
public class IncrementalInvalidationTests
{
    private static async Task<Project> AspxProjectAsync() =>
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.AspxProjectFile);

    /// <summary>The parse results by file, so a test can ask whether a given page was reused.</summary>
    private static Dictionary<string, AspxParseResult> ByPath(AspxProjectIndex index) =>
        index.Files.ToDictionary(f => Path.GetFullPath(f.FilePath), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Undoes a buffer edit made to a shared fixture, cache included.
    /// </summary>
    /// <remarks>
    /// Closing the buffer restores the text but not what was derived from it while it was open: a
    /// markup index built against the edited compilation stays cached for the project, and every
    /// later test in this collection reads the same process-wide cache. That is not hypothetical —
    /// leaving it behind broke go-to-definition from a code block into markup, several classes
    /// later, in about half of the full runs and never in a filtered one.
    /// </remarks>
    private static async Task RevertAsync(string session, string file, string projectFile)
    {
        OpenDocumentStore.Close(session, file);
        ProjectIndexCacheService.InvalidateProject(projectFile);
        await WorkspaceService.EvictProjectForTests(projectFile);
    }

    private static void Notify(string project, string file, bool movedFiles) =>
        Assert.True(
            ProjectIndexCacheService.NotifyFileChangedForTests(project, file, movedFiles),
            $"'{project}' has no cache entry, so the change to '{file}' was not delivered and the "
            + "assertions below would be vacuous.");

    /// <summary>
    /// Editing one markup file re-parses that file and carries every other one over.
    /// </summary>
    /// <remarks>
    /// Every markup extension, not just <c>.ascx</c>: they share one index and one dirty flag, and
    /// a policy that special-cased <c>.aspx</c> while leaving <c>.master</c> whole-site would be
    /// invisible in a test that only ever edited a page.
    /// </remarks>
    [Theory]
    [InlineData("Default.aspx")]
    [InlineData(@"Controls\HeaderControl.ascx")]
    [InlineData("Site.master")]
    [InlineData("DataService.asmx")]
    [InlineData("ImageHandler.ashx")]
    public async Task EditingOneMarkupFileReparsesOnlyThatFile(string relativePath)
    {
        var project = await AspxProjectAsync();
        string file = Path.GetFullPath(Path.Combine(FixturePaths.AspxProjectDir, relativePath));

        // Two builds before the one being measured. The first creates the cache entry — there is
        // nothing to notify a change to before it exists — and may be answered from an index an
        // earlier test built, at a semantic version this project has since moved past. Carrying
        // parses across that is unsound and does not happen, so the baseline has to be an index
        // built at the version in force now; this is what forces one.
        await ProjectIndexCacheService.GetAspxIndexAsync(project);
        Notify(FixturePaths.AspxProjectFile, file, movedFiles: false);

        var before = ByPath(await ProjectIndexCacheService.GetAspxIndexAsync(project));
        Assert.True(
            before.ContainsKey(file),
            $"'{relativePath}' is not in the markup index, so this test would prove nothing.");

        Notify(FixturePaths.AspxProjectFile, file, movedFiles: false);

        var after = ByPath(await ProjectIndexCacheService.GetAspxIndexAsync(project));

        // The index still covers the whole site — an incremental update that quietly dropped files
        // would satisfy every reference check below.
        Assert.Equal(before.Keys.Order(), after.Keys.Order());

        Assert.False(
            ReferenceEquals(before[file], after[file]),
            $"'{relativePath}' changed and was not re-parsed.");

        foreach (var (path, parse) in before)
        {
            if (string.Equals(path, file, StringComparison.OrdinalIgnoreCase))
                continue;

            Assert.True(
                ReferenceEquals(parse, after[path]),
                $"Editing '{relativePath}' re-parsed '{Path.GetFileName(path)}', which it cannot "
                + "have changed. This is the whole-site rebuild coming back.");
        }
    }

    /// <summary>
    /// A declaration change rebuilds every parse rather than carrying the old ones over.
    /// </summary>
    /// <remarks>
    /// A parse is not a function of its own file. <c>&lt;asp:TextBox ID="x"&gt;</c> becomes a
    /// control with an id because the compilation says what <c>asp:TextBox</c> is, so results
    /// resolved against one set of types cannot be handed out once the types have moved. Keeping
    /// them broke go-to-definition from a code block into the markup that declares the control:
    /// the index still held parses whose controls had been resolved against a compilation that no
    /// longer applied, so the mapping back to the ID attribute found nothing and the answer fell
    /// back to the generated designer line.
    /// </remarks>
    [Fact]
    public async Task ADeclarationChangeRebuildsEveryParse()
    {
        string session = $"incremental-{Guid.NewGuid():N}";
        string codeBehind = FixturePaths.AspxPageHelperFile;
        string text = await File.ReadAllTextAsync(codeBehind);
        string markup = Path.GetFullPath(FixturePaths.HeaderControlFile);

        var project = await AspxProjectAsync();
        await ProjectIndexCacheService.GetAspxIndexAsync(project);
        Notify(FixturePaths.AspxProjectFile, markup, movedFiles: false);
        var before = ByPath(await ProjectIndexCacheService.GetAspxIndexAsync(project));
        Assert.NotEmpty(before);

        OpenDocumentStore.Open(session, codeBehind, SourceText.From(text), version: 1);
        try
        {
            // A type, not a statement: this is precisely the kind of edit that can change what a
            // tag prefix resolves to.
            OpenDocumentStore.Change(
                codeBehind,
                version: 2,
                _ => SourceText.From(text + "\nnamespace Added { public class Marker { } }\n"));

            var edited = await AspxProjectAsync();
            Notify(FixturePaths.AspxProjectFile, markup, movedFiles: false);

            var after = ByPath(await ProjectIndexCacheService.GetAspxIndexAsync(edited));

            int carried = before.Count(e => after.TryGetValue(e.Key, out var now) && ReferenceEquals(e.Value, now));
            Assert.True(
                carried == 0,
                $"{carried} of {before.Count} parses were carried across a declaration change.");
        }
        finally
        {
            await RevertAsync(session, codeBehind, FixturePaths.AspxProjectFile);
        }
    }

    /// <summary>
    /// A markup file appearing or disappearing rebuilds the index, and is meant to.
    /// </summary>
    /// <remarks>
    /// The deliberate exception, pinned so that a later attempt to make this incremental too has
    /// to say so out loud. Which files exist is not a per-file fact: the index is built from a
    /// directory walk, and carrying the previous one over would keep serving a page that is gone.
    /// </remarks>
    [Fact]
    public async Task AMarkupFileAppearingRebuildsTheIndex()
    {
        var project = await AspxProjectAsync();
        string file = Path.GetFullPath(FixturePaths.HeaderControlFile);

        var before = ByPath(await ProjectIndexCacheService.GetAspxIndexAsync(project));

        Notify(FixturePaths.AspxProjectFile, file, movedFiles: true);

        var after = ByPath(await ProjectIndexCacheService.GetAspxIndexAsync(project));

        Assert.All(before, entry => Assert.False(ReferenceEquals(entry.Value, after[entry.Key])));
    }

    /// <summary>
    /// <c>web.config</c> rebuilds every parse, and is meant to.
    /// </summary>
    /// <remarks>
    /// The other deliberate exception. It registers the tag prefixes every page binds through, so
    /// unlike a page edit it really does change what every other parse would produce.
    /// </remarks>
    [Fact]
    public async Task WebConfigRebuildsEveryParse()
    {
        var project = await AspxProjectAsync();

        var before = ByPath(await ProjectIndexCacheService.GetAspxIndexAsync(project));
        Assert.NotEmpty(before);

        Notify(FixturePaths.AspxProjectFile, FixturePaths.AspxWebConfigFile, movedFiles: false);

        var after = ByPath(await ProjectIndexCacheService.GetAspxIndexAsync(project));

        Assert.All(before, entry => Assert.False(ReferenceEquals(entry.Value, after[entry.Key])));
    }

    /// <summary>
    /// Editing a <c>.cs</c> leaves the markup index entirely alone — not even a rebuild that
    /// happens to produce equal results.
    /// </summary>
    /// <remarks>
    /// A code-behind edit cannot change how markup parses; what it changes is what the markup
    /// binds to, which the diagnostics sweep tracks separately through each page's own code-behind
    /// version. Treating every save as an index change is what made a site with a thousand pages
    /// re-parse all of them for one keystroke's worth of saved text.
    /// </remarks>
    [Fact]
    public async Task EditingCSharpLeavesTheMarkupIndexAlone()
    {
        var project = await AspxProjectAsync();

        var before = await ProjectIndexCacheService.GetAspxIndexAsync(project);

        Notify(FixturePaths.AspxProjectFile, FixturePaths.AspxPageHelperFile, movedFiles: false);

        var after = await ProjectIndexCacheService.GetAspxIndexAsync(project);

        Assert.True(ReferenceEquals(before, after), "A .cs edit rebuilt the markup index.");
    }

    /// <summary>
    /// Editing one <c>.cs</c> re-reads that file when looking for FindControl wrappers, and no
    /// others.
    /// </summary>
    /// <remarks>
    /// The list is project-wide and every <c>.cs</c> write marks it stale, so the fix is not to
    /// invalidate less but to make re-deriving it cheap: whether a document declares a wrapper is
    /// a function of its own text, so an unchanged one costs a version comparison instead of a
    /// whole-file string allocation and a syntax walk. The counter is the only place that is
    /// visible — the same wrappers come back either way.
    /// </remarks>
    [Fact]
    public async Task EditingOneCSharpFileRescansOnlyThatFileForWrappers()
    {
        var project = await AspxProjectAsync();

        // Warm every document into the memo, so the delta below is attributable to the edit.
        await ProjectIndexCacheService.GetFindControlWrappersAsync(project);

        Notify(FixturePaths.AspxProjectFile, FixturePaths.AspxPageHelperFile, movedFiles: false);

        long before = AspxSourceMappingService.AccessorScanCount;
        var wrappers = await ProjectIndexCacheService.GetFindControlWrappersAsync(project);
        long scanned = AspxSourceMappingService.AccessorScanCount - before;

        // The fixture declares wrappers; a scan that found none would make "nothing was re-read"
        // true for the wrong reason.
        Assert.NotEmpty(wrappers);

        // Zero, not one: nothing was written, so no document's text version actually moved. The
        // flag says "look again", and looking again is now what costs nothing.
        Assert.True(
            scanned == 0,
            $"A single .cs change re-read {scanned} documents' text and syntax; only files whose "
            + "text actually moved should be scanned.");
    }

    /// <summary>
    /// Typing into a <c>.cs</c> re-reads that one file, and only it.
    /// </summary>
    /// <remarks>
    /// The other half of <see cref="EditingOneCSharpFileRescansOnlyThatFileForWrappers"/>, and the
    /// one that keeps it honest: "nothing was re-read" is also what a memo that never notices an
    /// edit would report, and that memo would serve wrappers from text the user has replaced.
    /// Exactly one, so this fails both ways — from a memo that misses the edit and from one that
    /// re-reads the neighbours along with it.
    /// </remarks>
    [Fact]
    public async Task TypingInACSharpFileRescansThatFileAndNoOther()
    {
        string session = $"incremental-{Guid.NewGuid():N}";
        string path = FixturePaths.AspxPageHelperFile;
        string text = await File.ReadAllTextAsync(path);

        // Warm every document into the memo at its on-disk version.
        await ProjectIndexCacheService.GetFindControlWrappersAsync(await AspxProjectAsync());

        OpenDocumentStore.Open(session, path, SourceText.From(text), version: 1);
        try
        {
            OpenDocumentStore.Change(
                path, version: 2, _ => SourceText.From(text + "\n// keystroke\n"));

            var edited = await AspxProjectAsync();
            Notify(FixturePaths.AspxProjectFile, path, movedFiles: false);

            long before = AspxSourceMappingService.AccessorScanCount;
            await ProjectIndexCacheService.GetFindControlWrappersAsync(edited);
            long scanned = AspxSourceMappingService.AccessorScanCount - before;

            Assert.True(
                scanned == 1,
                $"One file was edited and {scanned} were re-read.");
        }
        finally
        {
            await RevertAsync(session, path, FixturePaths.AspxProjectFile);
        }
    }

    /// <summary>Editing markup re-reads no C# at all: the two indexes are independent.</summary>
    [Fact]
    public async Task EditingMarkupRescansNoCSharpForWrappers()
    {
        var project = await AspxProjectAsync();
        await ProjectIndexCacheService.GetFindControlWrappersAsync(project);

        Notify(FixturePaths.AspxProjectFile, FixturePaths.HeaderControlFile, movedFiles: false);

        long before = AspxSourceMappingService.AccessorScanCount;
        await ProjectIndexCacheService.GetFindControlWrappersAsync(project);

        Assert.Equal(before, AspxSourceMappingService.AccessorScanCount);
    }

    /// <summary>
    /// Editing one <c>.resx</c> drops the key tables of its own directory and leaves every other
    /// directory's alone.
    /// </summary>
    /// <remarks>
    /// The directory is the right unit here rather than the file, and this is the test that says
    /// why: a neutral <c>.resx</c> and its translations are one family, read together, and
    /// <c>App_LocalResources</c> is how a WebForms site groups them. Finer than a directory would
    /// not be more incremental, it would be wrong. Coarser — the whole catalog — is what this
    /// pins against.
    /// </remarks>
    [Fact]
    public void EditingOneResxDropsOnlyItsOwnDirectorysKeyTables()
    {
        var local = ResourceCatalogService.Get(FixturePaths.LocalResourcesDir);
        var global = ResourceCatalogService.Get(FixturePaths.GlobalResourcesDir);

        var localFamily = Assert.Single(local.Families, f => f.BaseName == "Localized.aspx");
        var globalFamily = Assert.Single(global.Families, f => f.BaseName == "Strings");

        // Loaded, so that there is something to lose: an unloaded family has no key tables and
        // would survive any invalidation at all.
        var localBefore = ResourceCatalogService.Load(localFamily);
        var globalBefore = ResourceCatalogService.Load(globalFamily);
        Assert.True(localBefore.KeysLoaded);
        Assert.True(globalBefore.KeysLoaded);

        ResourceCatalogService.InvalidateContent(FixturePaths.LocalizedResxFile);

        Assert.False(
            ReferenceEquals(localBefore, ResourceCatalogService.Load(localFamily)),
            "The edited file's own family kept its key tables and would serve stale keys.");

        Assert.True(
            ReferenceEquals(globalBefore, ResourceCatalogService.Load(globalFamily)),
            "Editing a file under App_LocalResources dropped App_GlobalResources' key tables too.");
    }

    /// <summary>
    /// A <c>.resx</c> appearing regroups the directories above it and no others.
    /// </summary>
    /// <remarks>
    /// Layout has to be coarser than content — which families exist is a function of the file
    /// names in a directory, so a new file can rename an existing family — but it is still scoped
    /// by containment rather than global.
    /// </remarks>
    [Fact]
    public void AResxAppearingRegroupsOnlyTheDirectoriesAboveIt()
    {
        var aspx = ResourceCatalogService.Get(FixturePaths.AspxProjectDir);
        var sample = ResourceCatalogService.Get(FixturePaths.SampleProjectDir);
        Assert.NotEmpty(aspx.Families);

        ResourceCatalogService.InvalidateLayout(
            Path.Combine(FixturePaths.LocalResourcesDir, "Appeared.aspx.resx"));

        Assert.False(
            ReferenceEquals(aspx, ResourceCatalogService.Get(FixturePaths.AspxProjectDir)),
            "A .resx appeared under this directory and its grouping was not redone.");

        Assert.True(
            ReferenceEquals(sample, ResourceCatalogService.Get(FixturePaths.SampleProjectDir)),
            "A .resx under AspxProject regrouped an unrelated project's resources.");
    }

    /// <summary>
    /// Editing one <c>.proto</c> rebuilds the import graph without re-reading the others.
    /// </summary>
    /// <remarks>
    /// The graph is the one index here that is legitimately whole-project — an <c>import</c> is a
    /// relationship between two files, so which files import a given one cannot be worked out from
    /// any single file — and it is rebuilt in full on every <c>.proto</c> change. What keeps that
    /// cheap is that the rebuild reads no files: each <c>.proto</c>'s parse is cached against its
    /// stamp and checksum, so re-walking the imports is dictionary work over parses that are
    /// already in hand. This pins that, because the alternative reading of "the graph is rebuilt"
    /// is that every <c>.proto</c> in the project is re-read to do it.
    /// </remarks>
    [Fact]
    public async Task EditingOneProtoRebuildsTheGraphWithoutRereadingTheOthers()
    {
        var project = await RoslynTestHelpers.OpenProjectAsync(FixturePaths.ProtoProjectFile);

        var first = await ProjectIndexCacheService.GetProtoImportGraphAsync(project);
        Assert.NotEmpty(first.Files);

        var untouched = ProtoDocumentService.GetParse(FixturePaths.WidgetTypesProtoFile);
        Assert.NotNull(untouched);

        Notify(FixturePaths.ProtoProjectFile, FixturePaths.CommonTypesProtoFile, movedFiles: false);

        var second = await ProjectIndexCacheService.GetProtoImportGraphAsync(project);

        // The graph really was rebuilt rather than served from cache, or the parse below would
        // have survived for the uninteresting reason.
        Assert.False(ReferenceEquals(first, second));

        Assert.True(
            ReferenceEquals(untouched, ProtoDocumentService.GetParse(FixturePaths.WidgetTypesProtoFile)),
            "Rebuilding the import graph re-read a .proto whose text had not moved.");
    }

    /// <summary>
    /// Rebuilding the Razor source map re-reads only the generated documents that moved.
    /// </summary>
    /// <remarks>
    /// The map itself is still reassembled from scratch — which documents a <c>.razor</c>
    /// generates is not a per-document fact — but the <c>#line</c> scan of each generated
    /// document is what the time went into, and that is a function of its own text.
    /// </remarks>
    [Fact]
    public async Task RebuildingTheRazorMapRereadsOnlyGeneratedDocumentsThatMoved()
    {
        var project = await RoslynTestHelpers.OpenProjectAsync(FixturePaths.BlazorProjectFile);

        // Three builds, not two. The first creates the cache entry — there is nothing to notify a
        // change to before it exists — and may be answered from a map an earlier test left behind.
        // The second is a real rebuild against this project, which is what fills the memo. Only
        // then is there an incremental rebuild to measure.
        await ProjectIndexCacheService.GetRazorSourceMapAsync(project);

        Notify(FixturePaths.BlazorProjectFile, FixturePaths.CounterRazorFile, movedFiles: false);
        var first = await ProjectIndexCacheService.GetRazorSourceMapAsync(project);
        Assert.NotEmpty(first.Mappings);

        Notify(FixturePaths.BlazorProjectFile, FixturePaths.CounterRazorFile, movedFiles: false);

        long before = RazorSourceMappingService.LineMappingParseCount;
        var second = await ProjectIndexCacheService.GetRazorSourceMapAsync(project);
        long parsed = RazorSourceMappingService.LineMappingParseCount - before;

        Assert.NotEmpty(second.Mappings);
        Assert.True(
            parsed == 0,
            $"A Razor map rebuild re-read {parsed} generated documents whose text had not moved.");
    }
}

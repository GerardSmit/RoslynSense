using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Languages;
using RoslynMCP.Languages.WebForms;
using RoslynMCP.Languages.WebForms.Core;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>workspace/didChangeWatchedFiles: external changes must reach the loaded workspace
/// without a manual reload, and a burst must not reload once per event.</summary>
[Collection(SharedState.Name)]
public class WatchedFilesTests
{
    [Fact]
    public async Task SourceFileCreatedOnDiskBecomesResolvableWithoutManualReload()
    {
        // Load the project so a stale snapshot exists to invalidate.
        await RoslynTestHelpers.OpenProjectAsync(
            FixturePaths.SampleProjectFile, FixturePaths.CalculatorFile);

        string newFile = Path.Combine(FixturePaths.SampleProjectDir, $"WatchedAdded{Guid.NewGuid():N}.cs");
        await File.WriteAllTextAsync(newFile, """
            namespace SampleProject;

            public sealed class WatchedAddedType
            {
                public int Answer() => 42;
            }
            """);
        try
        {
            Assert.Null(await LspDocumentResolver.ResolveAsync(newFile, default));

            int loadedBefore = WorkspaceService.CachedEntryCount;

            var outcome = await RoslynTestHelpers.ProcessWatchedFilesAsync(
                [new FileEvent(LspConverters.PathToUri(newFile), FileChangeType.Created)], default);

            Assert.False(outcome.ReloadedWorkspace);
            Assert.NotNull(await LspDocumentResolver.ResolveAsync(newFile, default));

            // The file arrives without the solution being thrown away. Eviction is not the local
            // operation its name suggests: one cache entry serves a whole solution, so the old
            // behaviour discarded every compilation and analyzer result in it because one file
            // appeared — which is what a branch switch or a scaffold did.
            Assert.Contains(newFile, outcome.AppliedDocumentChanges ?? [], StringComparer.OrdinalIgnoreCase);
            Assert.Empty(outcome.EvictedProjects);
            Assert.Equal(loadedBefore, WorkspaceService.CachedEntryCount);
        }
        finally
        {
            File.Delete(newFile);
            await WorkspaceService.EvictAllAsync();
        }
    }

    /// <summary>
    /// A project file really does need MSBuild again — for the project it shapes.
    /// </summary>
    /// <remarks>
    /// References, analyzers and compile items all come from evaluation, so there is no reasoning
    /// about a <c>.csproj</c> change in place. What was wrong was the reach: one project file
    /// reloaded every solution the process had open, including ones in other windows sharing
    /// nothing with it. A <c>.sln</c> or an imported <c>.props</c> still takes everything, because
    /// those genuinely can shape every project that sees them.
    /// </remarks>
    [Fact]
    public async Task ProjectFileChangeReloadsThatProjectsWorkspace()
    {
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);
        Assert.True(WorkspaceService.IsProjectCachedForTests(FixturePaths.SampleProjectFile));

        var outcome = await RoslynTestHelpers.ProcessWatchedFilesAsync(
            [new FileEvent(LspConverters.PathToUri(FixturePaths.SampleProjectFile), FileChangeType.Changed)],
            default);

        Assert.True(outcome.ReloadedWorkspace);
        Assert.False(WorkspaceService.IsProjectCachedForTests(FixturePaths.SampleProjectFile));
    }

    [Fact]
    public async Task EditorConfigChangeReloadsAndClearsAnalyzerCache()
    {
        var (_, document) = await RoslynTestHelpers.OpenDocumentAsync(FixturePaths.WarningsFile);
        await AnalyzerDiagnosticCache.GetOrComputeAsync(document, default);
        string? version = await AnalyzerDiagnosticCache.GetVersionAsync(document, default);
        Assert.True(AnalyzerDiagnosticCache.IsComputed(document, version));

        string editorConfig = Path.Combine(FixturePaths.SampleProjectDir, ".editorconfig");
        var outcome = await RoslynTestHelpers.ProcessWatchedFilesAsync(
            [new FileEvent(LspConverters.PathToUri(editorConfig), FileChangeType.Changed)], default);

        // Severities live in the loaded project's analyzer options, and every cached result was
        // computed under the previous rules.
        Assert.True(outcome.ReloadedWorkspace);
        Assert.False(AnalyzerDiagnosticCache.IsComputed(document, version));
    }

    [Fact]
    public async Task MarkupChangedOnDiskAsksTheEditorToRefresh()
    {
        // Watched files are offered to the registered packs, and calling the handler directly
        // rather than through a server means no host has built a registry, so this stands in
        // for one.
        new LanguageRegistry([new WebFormsLanguage(new MarkdownFormatter())]).Publish();

        // A markup file is not a Roslyn document, so it evicts no project and reloads nothing.
        // What it must still do is report that something happened — otherwise the diagnostics
        // already on screen were computed from the old markup and nothing asks for them again.
        var outcome = await RoslynTestHelpers.ProcessWatchedFilesAsync(
            [new FileEvent(
                LspConverters.PathToUri(FixturePaths.EventWiringAspxFile), FileChangeType.Changed)],
            default);

        Assert.False(outcome.ReloadedWorkspace);
        Assert.Empty(outcome.EvictedProjects);
        Assert.True(outcome.DidAnything);
        Assert.Contains(
            FixturePaths.EventWiringAspxFile,
            outcome.InvalidatedMarkup ?? [],
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WebConfigChangeDropsEveryParseThatDependedOnIt()
    {
        new LanguageRegistry([new WebFormsLanguage(new MarkdownFormatter())]).Publish();

        // web.config registers tag prefixes, so it changes how every page binds without changing
        // any page. The parse cache keys on a file's own text and its compilation, neither of
        // which moves — so nothing short of clearing it is correct. Prime several markup files of
        // different kinds: a per-file Invalidate keyed on the web.config's own path would satisfy
        // a one-page assertion while leaving every other page bound against the old prefixes.
        string[] markup =
        [
            FixturePaths.DefaultAspxFile,
            FixturePaths.DesignerAspxFile,
            FixturePaths.SiteMasterFile,
            FixturePaths.HeaderControlFile,
        ];

        var before = new Dictionary<string, AspxDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in markup)
        {
            var document = await AspxDocumentService.GetAsync(path, default);
            Assert.NotNull(document);
            before[path] = document;
        }

        var outcome = await RoslynTestHelpers.ProcessWatchedFilesAsync(
            [new FileEvent(
                LspConverters.PathToUri(FixturePaths.AspxWebConfigFile), FileChangeType.Changed)],
            default);

        Assert.True(outcome.DidAnything);

        // Nothing was reparsed yet, so an unchanged instance here means the entry survived the
        // event — the page would keep reporting a newly registered control as unknown.
        foreach (string path in markup)
        {
            var after = await AspxDocumentService.GetAsync(path, default);
            Assert.NotNull(after);
            Assert.NotSame(before[path], after);
        }
    }

    [Fact]
    public async Task BuildOutputEventsAreIgnored()
    {
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);
        int before = WorkspaceService.CachedEntryCount;

        var outcome = await RoslynTestHelpers.ProcessWatchedFilesAsync(
        [
            new FileEvent(LspConverters.PathToUri(
                Path.Combine(FixturePaths.SampleProjectDir, "obj", "Debug", "Generated.cs")),
                FileChangeType.Created),
            new FileEvent(LspConverters.PathToUri(
                Path.Combine(FixturePaths.SampleProjectDir, "bin", "Debug", "App.csproj")),
                FileChangeType.Changed),
        ], default);

        Assert.False(outcome.DidAnything);
        Assert.Equal(before, WorkspaceService.CachedEntryCount);
    }

    [Fact]
    public async Task BurstOfSourceEventsDoesNotDiscardTheLoadedSolution()
    {
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);
        int loadedBefore = WorkspaceService.CachedEntryCount;
        Assert.True(loadedBefore > 0);

        // None of these exist on disk — the shape a branch switch produces, where events arrive
        // for files that are already gone again by the time they are processed.
        var events = Enumerable.Range(0, 50)
            .Select(i => new FileEvent(
                LspConverters.PathToUri(Path.Combine(FixturePaths.SampleProjectDir, $"Burst{i}.cs")),
                FileChangeType.Created))
            .ToArray();

        var outcome = await RoslynTestHelpers.ProcessWatchedFilesAsync(events, default);

        // 50 events used to mean the solution's workspace was discarded. It must survive, and no
        // document may be invented for a file that is not there — a loader over a missing file
        // throws the first time anything reads the document.
        Assert.Empty(outcome.EvictedProjects);
        Assert.Equal(loadedBefore, WorkspaceService.CachedEntryCount);

        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(FixturePaths.SampleProjectFile);
        Assert.DoesNotContain(project.Documents, d =>
            d.FilePath is { Length: > 0 } fp && Path.GetFileName(fp).StartsWith("Burst", StringComparison.Ordinal));
    }

    /// <summary>
    /// Deleting a source file removes the document, and still leaves the solution loaded.
    /// </summary>
    [Fact]
    public async Task SourceFileDeletedOnDiskDropsTheDocumentWithoutDiscardingTheSolution()
    {
        string newFile = Path.Combine(FixturePaths.SampleProjectDir, $"WatchedGone{Guid.NewGuid():N}.cs");
        await File.WriteAllTextAsync(newFile, """
            namespace SampleProject;

            public sealed class WatchedGoneType
            {
                public int Answer() => 42;
            }
            """);

        try
        {
            await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);
            await RoslynTestHelpers.ProcessWatchedFilesAsync(
                [new FileEvent(LspConverters.PathToUri(newFile), FileChangeType.Created)], default);
            Assert.NotNull(await LspDocumentResolver.ResolveAsync(newFile, default));

            int loadedBefore = WorkspaceService.CachedEntryCount;
            File.Delete(newFile);

            var outcome = await RoslynTestHelpers.ProcessWatchedFilesAsync(
                [new FileEvent(LspConverters.PathToUri(newFile), FileChangeType.Deleted)], default);

            Assert.Empty(outcome.EvictedProjects);
            Assert.Equal(loadedBefore, WorkspaceService.CachedEntryCount);

            var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(FixturePaths.SampleProjectFile);
            Assert.DoesNotContain(project.Documents, d =>
                d.FilePath is { Length: > 0 } fp
                && string.Equals(Path.GetFullPath(fp), newFile, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (File.Exists(newFile))
                File.Delete(newFile);
            await WorkspaceService.EvictAllAsync();
        }
    }

    /// <summary>
    /// A tool rewriting the same files repeatedly costs one operation per file, not per write.
    /// </summary>
    /// <remarks>
    /// An agent working through a change, a formatter, or a generator produces an event per write.
    /// Processing each one separately makes the work proportional to how much the tool wrote rather
    /// than to how much actually changed.
    /// </remarks>
    [Fact]
    public void RepeatedWritesToTheSameFileCollapseToOneEvent()
    {
        string a = LspConverters.PathToUri(Path.Combine(FixturePaths.SampleProjectDir, "A.cs"));
        string b = LspConverters.PathToUri(Path.Combine(FixturePaths.SampleProjectDir, "B.cs"));

        var collapsed = WatchedFilesHandler.Collapse(
        [
            new FileEvent(a, FileChangeType.Changed),
            new FileEvent(a, FileChangeType.Changed),
            new FileEvent(a, FileChangeType.Changed),
            new FileEvent(b, FileChangeType.Created),
        ]);

        Assert.Equal(2, collapsed.Length);
        Assert.Single(collapsed, e => e.Uri == a);
        Assert.Single(collapsed, e => e.Uri == b);
    }

    [Fact]
    public void ACreateFollowedByADeleteCollapsesToTheDelete()
    {
        string uri = LspConverters.PathToUri(Path.Combine(FixturePaths.SampleProjectDir, "Gone.cs"));

        var collapsed = WatchedFilesHandler.Collapse(
        [
            new FileEvent(uri, FileChangeType.Created),
            new FileEvent(uri, FileChangeType.Changed),
            new FileEvent(uri, FileChangeType.Deleted),
        ]);

        // On the far side of the batch the file is gone; treating it as created would add a
        // document whose loader throws the first time anything reads it.
        Assert.Equal(FileChangeType.Deleted, Assert.Single(collapsed).Type);
    }

    /// <summary>
    /// The server's own writes are not outside changes.
    /// </summary>
    /// <remarks>
    /// Every mutating operation invalidates what it changed and then writes a .sln or .csproj. The
    /// watcher reports that write back, and without this it cannot be told apart from someone
    /// editing the project in another editor — so each operation cost a second full reload.
    /// </remarks>
    [Fact]
    public async Task TheServersOwnProjectFileWriteDoesNotReloadTheWorkspace()
    {
        SelfWriteTracker.ResetForTests();
        try
        {
            await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);
            Assert.True(WorkspaceService.CachedEntryCount > 0);

            SelfWriteTracker.Note(FixturePaths.SampleProjectFile);

            var outcome = await RoslynTestHelpers.ProcessWatchedFilesAsync(
                [new FileEvent(LspConverters.PathToUri(FixturePaths.SampleProjectFile), FileChangeType.Changed)],
                default);

            Assert.False(outcome.ReloadedWorkspace);
            Assert.True(WorkspaceService.CachedEntryCount > 0);
        }
        finally
        {
            SelfWriteTracker.ResetForTests();
        }
    }

    /// <summary>
    /// An outside edit to a closed file reaches the workspace without discarding it.
    /// </summary>
    /// <remarks>
    /// A modification is a text change, not a document-set change, so no MSBuild re-evaluation is
    /// needed — but the text does have to arrive, or the workspace keeps binding against what the
    /// file said before the checkout.
    /// </remarks>
    [Fact]
    public async Task AnExternalEditToAClosedFileReachesTheWorkspace()
    {
        string file = Path.Combine(FixturePaths.SampleProjectDir, $"WatchedEdited{Guid.NewGuid():N}.cs");
        await File.WriteAllTextAsync(file, "namespace SampleProject; public sealed class BeforeEdit { }");

        try
        {
            await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);
            await RoslynTestHelpers.ProcessWatchedFilesAsync(
                [new FileEvent(LspConverters.PathToUri(file), FileChangeType.Created)], default);

            await File.WriteAllTextAsync(file, "namespace SampleProject; public sealed class AfterEdit { }");

            int loadedBefore = WorkspaceService.CachedEntryCount;
            await RoslynTestHelpers.ProcessWatchedFilesAsync(
                [new FileEvent(LspConverters.PathToUri(file), FileChangeType.Changed)], default);

            var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
                FixturePaths.SampleProjectFile, targetFilePath: file);
            var document = WorkspaceService.FindDocumentInProject(project, file);
            Assert.NotNull(document);
            Assert.Contains("AfterEdit", (await document!.GetTextAsync()).ToString());

            // And without paying for it with a reload of everything else.
            Assert.Equal(loadedBefore, WorkspaceService.CachedEntryCount);
        }
        finally
        {
            if (File.Exists(file))
                File.Delete(file);
            await WorkspaceService.EvictAllAsync();
        }
    }

    /// <summary>
    /// Saving a file in a .NET Framework project updates it in place, like anywhere else.
    /// </summary>
    /// <remarks>
    /// Legacy projects list their compile items explicitly, so only MSBuild can say whether a file
    /// that just appeared is compiled. That is true of an appearing file and of nothing else: a
    /// document already in the project whose text changed needs no evaluation to reason about.
    /// Refusing the whole class meant every save in a WebForms or Framework solution evicted the
    /// workspace and reloaded it — which is most of what "it reloads constantly" was, for anyone
    /// not on SDK-style projects.
    /// </remarks>
    [RequiresVisualStudioFact]
    public async Task SavingAFileInALegacyProjectDoesNotReloadTheWorkspace()
    {
        string file = FixturePaths.LegacyCalculatorFile;
        string original = await File.ReadAllTextAsync(file);

        try
        {
            await RoslynTestHelpers.OpenProjectAsync(FixturePaths.LegacyProjectFile, file);
            int loadedBefore = WorkspaceService.CachedEntryCount;
            Assert.True(loadedBefore > 0);

            await File.WriteAllTextAsync(file, original + "\n// saved from outside the editor\n");

            var outcome = await RoslynTestHelpers.ProcessWatchedFilesAsync(
                [new FileEvent(LspConverters.PathToUri(file), FileChangeType.Changed)], default);

            Assert.Empty(outcome.EvictedProjects);
            Assert.Equal(loadedBefore, WorkspaceService.CachedEntryCount);

            var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
                FixturePaths.LegacyProjectFile, targetFilePath: file);
            var document = WorkspaceService.FindDocumentInProject(project, file);
            Assert.NotNull(document);
            Assert.Contains("saved from outside the editor", (await document!.GetTextAsync()).ToString());
        }
        finally
        {
            await File.WriteAllTextAsync(file, original);
            await WorkspaceService.EvictAllAsync();
        }
    }

    /// <summary>
    /// One project file changing reloads that project, not every solution in the process.
    /// </summary>
    [RequiresVisualStudioFact]
    public async Task AProjectFileChangeReloadsOnlyItsOwnWorkspace()
    {
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.LegacyProjectFile);
        Assert.True(WorkspaceService.IsProjectCachedForTests(FixturePaths.LegacyProjectFile));

        var outcome = await RoslynTestHelpers.ProcessWatchedFilesAsync(
            [new FileEvent(LspConverters.PathToUri(FixturePaths.SampleProjectFile), FileChangeType.Changed)],
            default);

        Assert.True(outcome.ReloadedWorkspace);
        Assert.False(WorkspaceService.IsProjectCachedForTests(FixturePaths.SampleProjectFile));

        // The unrelated solution keeps its compilations and its analyzer results.
        Assert.True(WorkspaceService.IsProjectCachedForTests(FixturePaths.LegacyProjectFile));
    }

    /// <summary>
    /// The watcher echo of an ordinary save invalidates nothing at all.
    /// </summary>
    /// <remarks>
    /// A save produces a Changed event for a file whose text already reached the workspace on
    /// didChange — the event is a restatement, not information. An open buffer outranks disk, so
    /// the apply path answers NothingToDo, the outcome reports nothing happened, and no
    /// RefreshKind.All goes out — which is what keeps a save from costing a re-pull of every open
    /// document plus a workspace sweep. The semantic version is the deep assertion: had the loader
    /// been re-applied, every analyzer result and diagnostic result id in the project would have
    /// silently gone stale.
    /// </remarks>
    [Fact]
    public async Task SavingAnOpenFileChangesNothing()
    {
        string session = $"watched-save-{Guid.NewGuid():N}";
        string file = FixturePaths.CalculatorFile;

        try
        {
            await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile, file);
            OpenDocumentStore.Open(
                session, file, Microsoft.CodeAnalysis.Text.SourceText.From(await File.ReadAllTextAsync(file)), 1);

            var (_, before) = await WorkspaceService.GetOrOpenProjectAsync(
                FixturePaths.SampleProjectFile, targetFilePath: file);
            var semanticBefore = await before.GetDependentSemanticVersionAsync();

            var outcome = await RoslynTestHelpers.ProcessWatchedFilesAsync(
                [new FileEvent(LspConverters.PathToUri(file), FileChangeType.Changed)], default);

            Assert.False(outcome.DidAnything);

            var (_, after) = await WorkspaceService.GetOrOpenProjectAsync(
                FixturePaths.SampleProjectFile, targetFilePath: file);
            Assert.Equal(semanticBefore, await after.GetDependentSemanticVersionAsync());
        }
        finally
        {
            OpenDocumentStore.CloseSession(session);
            await WorkspaceService.EvictAllAsync();
        }
    }

    /// <summary>
    /// A project that lists its compile items never gains a document from the watcher.
    /// </summary>
    /// <remarks>
    /// Two projects can share one directory, and the watcher names every project in the changed
    /// file's directory — so both are asked to apply every event there. For the project with
    /// <c>EnableDefaultCompileItems=false</c> the answer must always be "not mine": treating a
    /// Changed event for a file it lacks as an arrival, or reading its own explicit
    /// <c>&lt;Compile Include&gt;</c> as evidence that a glob reaches the directory, both invented
    /// a document in a project that does not compile the file — and every type the file declares
    /// was then defined twice, as errors in a project nobody edited.
    /// </remarks>
    [Fact]
    public async Task AnExplicitItemsProjectNeverGainsADocumentFromTheWatcher()
    {
        string session = $"watched-explicit-{Guid.NewGuid():N}";
        string open = FixturePaths.CalculatorFile;
        string created = Path.Combine(FixturePaths.SampleProjectDir, $"WatchedExplicit{Guid.NewGuid():N}.cs");

        static async Task<List<string>> AardvarkDocumentsAsync()
        {
            var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(FixturePaths.AlternateProjectFile);
            return [.. project.Documents
                .Select(d => Path.GetFileName(d.FilePath ?? ""))
                .Where(n => n.Length > 0)
                .Order(StringComparer.OrdinalIgnoreCase)];
        }

        try
        {
            await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile, open);
            var before = await AardvarkDocumentsAsync();
            Assert.Contains("AardvarkExternal.cs", before);

            OpenDocumentStore.Open(
                session, open, Microsoft.CodeAnalysis.Text.SourceText.From(await File.ReadAllTextAsync(open)), 1);

            // The save echo: a Changed event for a file Aardvark has no document for.
            var save = await RoslynTestHelpers.ProcessWatchedFilesAsync(
                [new FileEvent(LspConverters.PathToUri(open), FileChangeType.Changed)], default);
            Assert.False(save.DidAnything);

            // The scaffold: a Created event beside Aardvark's explicit items. SampleProject globs
            // the directory and takes the file; Aardvark must not.
            await File.WriteAllTextAsync(created,
                "namespace SampleProject; public sealed class WatchedExplicitTarget { }");
            await RoslynTestHelpers.ProcessWatchedFilesAsync(
                [new FileEvent(LspConverters.PathToUri(created), FileChangeType.Created)], default);

            Assert.Equal(before, await AardvarkDocumentsAsync());
        }
        finally
        {
            OpenDocumentStore.CloseSession(session);
            if (File.Exists(created))
                File.Delete(created);
            await WorkspaceService.EvictAllAsync();
        }
    }

    /// <summary>
    /// Rewriting a closed file with the content it already had costs nothing.
    /// </summary>
    /// <remarks>
    /// A formatter that reformats to the same text, a generator re-emitting identical output, and a
    /// checkout restoring a file to what it already said all produce a change event for content
    /// that did not change. Re-applying the loader would give the document a new version and move
    /// its project's dependent semantic version, invalidating the analyzer results and diagnostic
    /// result ids of every file in it.
    /// </remarks>
    [Fact]
    public async Task RewritingAClosedFileWithIdenticalContentChangesNothing()
    {
        string file = Path.Combine(FixturePaths.SampleProjectDir, $"WatchedSame{Guid.NewGuid():N}.cs");
        const string Content = "namespace SampleProject; public sealed class SameContent { }";
        await File.WriteAllTextAsync(file, Content);

        try
        {
            await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);
            await RoslynTestHelpers.ProcessWatchedFilesAsync(
                [new FileEvent(LspConverters.PathToUri(file), FileChangeType.Created)], default);

            var (_, before) = await WorkspaceService.GetOrOpenProjectAsync(
                FixturePaths.SampleProjectFile, targetFilePath: file);
            var semanticBefore = await before.GetDependentSemanticVersionAsync();

            // Same bytes, new timestamp.
            await Task.Delay(20);
            await File.WriteAllTextAsync(file, Content);

            var outcome = await RoslynTestHelpers.ProcessWatchedFilesAsync(
                [new FileEvent(LspConverters.PathToUri(file), FileChangeType.Changed)], default);

            Assert.Empty(outcome.AppliedDocumentChanges ?? []);
            Assert.Empty(outcome.EvictedProjects);

            var (_, after) = await WorkspaceService.GetOrOpenProjectAsync(
                FixturePaths.SampleProjectFile, targetFilePath: file);
            Assert.Equal(semanticBefore, await after.GetDependentSemanticVersionAsync());
        }
        finally
        {
            if (File.Exists(file))
                File.Delete(file);
            await WorkspaceService.EvictAllAsync();
        }
    }

    /// <summary>
    /// A file created in a legacy project is not compiled until the project says so, and finding
    /// that out must not cost a reload.
    /// </summary>
    [RequiresVisualStudioFact]
    public async Task CreatingAFileInALegacyProjectDoesNotReloadTheWorkspace()
    {
        string file = Path.Combine(FixturePaths.LegacyProjectDir, $"LegacyNew{Guid.NewGuid():N}.cs");

        try
        {
            await RoslynTestHelpers.OpenProjectAsync(FixturePaths.LegacyProjectFile);
            int loadedBefore = WorkspaceService.CachedEntryCount;

            await File.WriteAllTextAsync(file, "namespace LegacyProject { public class Fresh { } }");

            var outcome = await RoslynTestHelpers.ProcessWatchedFilesAsync(
                [new FileEvent(LspConverters.PathToUri(file), FileChangeType.Created)], default);

            // A legacy project compiles what it lists. The file is not listed, so reloading would
            // spend seconds of MSBuild reaching the same answer.
            Assert.Empty(outcome.EvictedProjects);
            Assert.Equal(loadedBefore, WorkspaceService.CachedEntryCount);
        }
        finally
        {
            if (File.Exists(file))
                File.Delete(file);
            await WorkspaceService.EvictAllAsync();
        }
    }

    /// <summary>
    /// Excluding a file from a project drops its document, even though the file stays on disk.
    /// </summary>
    /// <remarks>
    /// The in-place delete path refuses to remove a document whose file still exists, because
    /// almost every such event is a file being replaced rather than removed. "Exclude from project"
    /// is the exception, and the caller is the one that knows — it just wrote the
    /// <c>Compile Remove</c>. Without that distinction the file stayed compiled forever: the
    /// removal silently did nothing, its "nothing to do" answer suppressed the eviction fallback,
    /// and the project-file write that would have caught it is filtered as our own echo.
    /// </remarks>
    [Fact]
    public async Task ExcludingAFileStillOnDiskRemovesItsDocument()
    {
        string file = Path.Combine(FixturePaths.SampleProjectDir, $"WatchedExcluded{Guid.NewGuid():N}.cs");
        await File.WriteAllTextAsync(file, "namespace SampleProject; public sealed class Excluded { }");

        try
        {
            await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);
            await RoslynTestHelpers.ProcessWatchedFilesAsync(
                [new FileEvent(LspConverters.PathToUri(file), FileChangeType.Created)], default);
            Assert.NotNull(await LspDocumentResolver.ResolveAsync(file, default));

            var result = await WorkspaceService.TryApplyFileChangeAsync(
                FixturePaths.SampleProjectFile, file, FileChange.Deleted, default, authoritative: true);

            Assert.Equal(FileSyncResult.Applied, result);

            var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(FixturePaths.SampleProjectFile);
            Assert.DoesNotContain(project.Documents, d =>
                d.FilePath is { Length: > 0 } fp
                && string.Equals(Path.GetFullPath(fp), file, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (File.Exists(file))
                File.Delete(file);
            await WorkspaceService.EvictAllAsync();
        }
    }

    /// <summary>
    /// A delete event for a file that is still there is a replacement, and its new text is read.
    /// </summary>
    /// <remarks>
    /// Writers that replace a file by unlinking and recreating it, or by renaming a temporary over
    /// it, emit a delete for a file that never went away. Discarding that event lost the write.
    /// </remarks>
    [Fact]
    public async Task ADeleteEventForAFileThatStillExistsReadsItsNewText()
    {
        string file = Path.Combine(FixturePaths.SampleProjectDir, $"WatchedReplaced{Guid.NewGuid():N}.cs");
        await File.WriteAllTextAsync(file, "namespace SampleProject; public sealed class BeforeReplace { }");

        try
        {
            await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);
            await RoslynTestHelpers.ProcessWatchedFilesAsync(
                [new FileEvent(LspConverters.PathToUri(file), FileChangeType.Created)], default);

            await File.WriteAllTextAsync(file, "namespace SampleProject; public sealed class AfterReplace { }");

            await RoslynTestHelpers.ProcessWatchedFilesAsync(
                [new FileEvent(LspConverters.PathToUri(file), FileChangeType.Deleted)], default);

            var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
                FixturePaths.SampleProjectFile, targetFilePath: file);
            var document = WorkspaceService.FindDocumentInProject(project, file);

            Assert.NotNull(document);
            Assert.Contains("AfterReplace", (await document!.GetTextAsync()).ToString());
        }
        finally
        {
            if (File.Exists(file))
                File.Delete(file);
            await WorkspaceService.EvictAllAsync();
        }
    }

    [Fact]
    public void NearestProjectFilesWalkUpAndReturnEveryCandidate()
    {
        string nested = Path.Combine(FixturePaths.SampleProjectDir, "Models", "Result.cs");

        var projects = WatchedFilesHandler.FindNearestProjectFiles(nested);

        // The fixture folder holds two projects; picking one arbitrarily would evict the wrong
        // snapshot and leave the stale one answering requests.
        Assert.Contains(FixturePaths.SampleProjectFile, projects, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(FixturePaths.AlternateProjectFile, projects, StringComparer.OrdinalIgnoreCase);

        Assert.Empty(WatchedFilesHandler.FindNearestProjectFiles(
            Path.Combine(Path.GetTempPath(), $"no-project-{Guid.NewGuid():N}", "Orphan.cs")));
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

[Collection(SharedState.Name)]
public class OpenDocumentOverlayTests
{
    [Fact]
    public void KnownBufferEqualitySurvivesDocumentMetadataChangesButNotTextChanges()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("BufferIdentity", LanguageNames.CSharp);
        var text = SourceText.From("class C { }");
        var document = workspace.AddDocument(project.Id, "Before.cs", text);
        WorkspaceService.RememberMatchingOpenText(document, text);
        var renamed = document.WithName("After.cs");
        Assert.NotSame(document.State, renamed.State);
        Assert.Same(document.State.TextAndVersionSource, renamed.State.TextAndVersionSource);
        Assert.True(WorkspaceService.HasMatchingOpenText(renamed, text));
        Assert.False(WorkspaceService.HasMatchingOpenText(renamed.WithText(SourceText.From("class D { }")), text));
    }

    [Fact]
    public async Task ClosingAnotherOverlaidProjectPreservesTheUnchangedCompilation()
    {
        string session = Guid.NewGuid().ToString("N");
        string first = FixturePaths.MultiProjectAClassFile;
        string second = FixturePaths.MultiProjectBClassFile;
        using var binding = WorkspaceService.BindSolutionForTesting(FixturePaths.MultiSolutionFile);
        await WorkspaceService.EnsureProjectsLoadedAsync([FixturePaths.MultiProjectAFile, FixturePaths.MultiProjectBFile]);
        var initial = WorkspaceService.TryGetSessionSolution()!;
        foreach (string path in new[] { first, second })
            await initial.GetDocument(initial.GetDocumentIdsWithFilePath(path).Single())!.GetTextAsync();
        var bridge = OpenDocumentStore.OverlayableBufferChanged;
        OpenDocumentStore.OverlayableBufferChanged = null; // Keep changes in the overlay, not the base workspace.
        try
        {
            OpenDocumentStore.Open(session, first, SourceText.From(await File.ReadAllTextAsync(first) + "\n// first overlay"), 1);
            OpenDocumentStore.Open(session, second, SourceText.From(await File.ReadAllTextAsync(second) + "\n// second overlay"), 1);
            var before = WorkspaceService.TryGetSessionSolution()!;
            var firstDocument = before.GetDocument(before.GetDocumentIdsWithFilePath(first).Single())!;
            var compilation = await firstDocument.Project.GetCompilationAsync();
            var version = await firstDocument.GetTextVersionAsync();
            OpenDocumentStore.Close(session, second);
            var after = WorkspaceService.TryGetSessionSolution()!;
            var kept = after.GetDocument(firstDocument.Id)!;
            Assert.Equal(version, await kept.GetTextVersionAsync());
            Assert.Same(compilation, await kept.Project.GetCompilationAsync());
            var closed = after.GetDocument(after.GetDocumentIdsWithFilePath(second).Single())!;
            Assert.Equal(await File.ReadAllTextAsync(second), (await closed.GetTextAsync()).ToString());
        }
        finally
        {
            OpenDocumentStore.Close(session, first);
            OpenDocumentStore.Close(session, second);
            OpenDocumentStore.OverlayableBufferChanged = bridge;
            await WorkspaceService.ReconcileOpenBufferAsync(first);
            await WorkspaceService.ReconcileOpenBufferAsync(second);
        }
    }

    [Fact]
    public async Task SnapshotSeesOpenBufferTextAndRevertsOnClose()
    {
        string path = FixturePaths.CalculatorFile;
        string diskText = await File.ReadAllTextAsync(path);
        string bufferText = diskText + "\n// overlay-marker: unsaved editor edit\n";
        string session = Guid.NewGuid().ToString("N");

        OpenDocumentStore.Open(session, path, SourceText.From(bufferText), version: 1);
        try
        {
            var text = await GetSnapshotTextAsync(path);
            Assert.Contains("overlay-marker", text);
        }
        finally
        {
            OpenDocumentStore.Close(session, path);
        }

        // Awaited, because the session's bridge reconciles off the message-loop thread — the
        // server schedules it and does not block didClose on it. The test would otherwise read the
        // snapshot while the buffer text is still in the workspace.
        await WorkspaceService.ReconcileOpenBufferAsync(path);

        var afterClose = await GetSnapshotTextAsync(path);
        Assert.DoesNotContain("overlay-marker", afterClose);
    }

    [Fact]
    public async Task OverlayAppliesToAllOpenDocumentsNotJustTarget()
    {
        // The overlay must cover every open buffer, so a request targeting file A still sees
        // unsaved edits in open file B (cross-file analysis correctness).
        string targetPath = FixturePaths.CalculatorFile;
        string otherPath = FixturePaths.ServicesFile;
        string otherDisk = await File.ReadAllTextAsync(otherPath);
        string session = Guid.NewGuid().ToString("N");

        OpenDocumentStore.Open(session, otherPath,
            SourceText.From(otherDisk + "\n// other-overlay-marker\n"), version: 1);
        try
        {
            var projectPath = await WorkspaceService.FindContainingProjectAsync(targetPath, default);
            Assert.NotNull(projectPath);
            var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
                projectPath!, targetFilePath: targetPath, cancellationToken: default);

            var otherDoc = WorkspaceService.FindDocumentInProject(project, otherPath);
            Assert.NotNull(otherDoc);
            var otherText = (await otherDoc!.GetTextAsync()).ToString();
            Assert.Contains("other-overlay-marker", otherText);
        }
        finally
        {
            OpenDocumentStore.Close(session, otherPath);
        }
    }

    [Fact]
    public void CloseSessionDropsOnlyDocumentsWithNoRemainingOwner()
    {
        string path = FixturePaths.CalculatorFile;
        var text = SourceText.From("// shared");

        OpenDocumentStore.Open("session-a", path, text, 1);
        OpenDocumentStore.Open("session-b", path, text, 1);

        OpenDocumentStore.CloseSession("session-a");
        Assert.True(OpenDocumentStore.IsOpen(path)); // session-b still owns it

        OpenDocumentStore.CloseSession("session-b");
        Assert.False(OpenDocumentStore.IsOpen(path));
    }

    /// <summary>
    /// Opening a markup file must not fork the solution.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An <c>.ascx</c> is not a Roslyn document, so the overlay can never apply it — but the store
    /// bumped one generation for every buffer, and the rebuild that followed produced a new
    /// <see cref="Microsoft.CodeAnalysis.Solution"/> whether or not any text actually moved. Every
    /// compilation went with it, and so did everything keyed on one: the memoized markup parses
    /// (which compare compilations by reference), and every document's dependent semantic version,
    /// which is half of the <c>workspace/diagnostic</c> result id. Closing and reopening one page
    /// therefore made the next pull report a whole website as changed and re-analyse it.
    /// </para>
    /// <para>
    /// Asserted on the compilation rather than on the counter, because the counter is the mechanism
    /// and this is the property that matters — a later mechanism that keeps the snapshot stable
    /// should keep this test passing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task OpeningAMarkupBufferLeavesTheCompilationSnapshotAlone()
    {
        string session = Guid.NewGuid().ToString("N");
        string markupPath = FixturePaths.DefaultAspxFile;

        var before = await GetCompilationAsync(FixturePaths.AspxProjectFile);

        OpenDocumentStore.Open(
            session, markupPath, SourceText.From(await File.ReadAllTextAsync(markupPath)), version: 1);
        try
        {
            Assert.Same(before, await GetCompilationAsync(FixturePaths.AspxProjectFile));
        }
        finally
        {
            OpenDocumentStore.Close(session, markupPath);
        }

        // And closing it again is the other half of the reported gesture.
        Assert.Same(before, await GetCompilationAsync(FixturePaths.AspxProjectFile));
    }

    /// <summary>
    /// Opening a second file must leave the documents that were already open alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the reported "opening any file reloads the whole solution": every warning and error
    /// in the window vanished and came back a moment later, with a progress notification over it.
    /// The overlay was rebuilt from the base solution on each open, and
    /// <c>WithDocumentText</c> compares by reference — the store's <see cref="SourceText"/> is never
    /// the instance the base solution loaded from disk, so every already-open buffer was re-applied
    /// and took a new version. Their projects' dependent semantic versions moved with them, which is
    /// half of the <c>textDocument/diagnostic</c> result id and the whole of the analyzer cache key,
    /// so the editor was told everything it held was stale and asked for all of it again.
    /// </para>
    /// <para>
    /// ProjectB references ProjectA and not the other way round, so opening a file in B has no
    /// legitimate claim on anything in A — which makes A's semantic version the assertion. Nothing
    /// here reaches for the memo counter: a different mechanism that keeps the snapshot stable
    /// should keep this test passing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task OpeningASecondFileLeavesTheFirstFilesSemanticVersionAlone()
    {
        string session = Guid.NewGuid().ToString("N");
        string fileInA = FixturePaths.MultiProjectAClassFile;
        string fileInB = FixturePaths.MultiProjectBClassFile;

        // Both projects into the one shared workspace up front: an incremental project load
        // legitimately invalidates everything, and that is not what is under test here.
        await WorkspaceService.GetOrOpenProjectAsync(FixturePaths.MultiProjectAFile);
        await WorkspaceService.GetOrOpenProjectAsync(FixturePaths.MultiProjectBFile);

        OpenDocumentStore.Open(
            session, fileInA, SourceText.From(await File.ReadAllTextAsync(fileInA)), version: 1);
        try
        {
            var (before, beforeText) = await SemanticAndTextVersionAsync(FixturePaths.MultiProjectAFile, fileInA);

            // The gesture: a second file, in another project, opens.
            OpenDocumentStore.Open(
                session, fileInB, SourceText.From(await File.ReadAllTextAsync(fileInB)), version: 1);
            try
            {
                var (after, afterText) = await SemanticAndTextVersionAsync(FixturePaths.MultiProjectAFile, fileInA);

                Assert.Equal(beforeText, afterText);
                Assert.Equal(before, after);
            }
            finally
            {
                OpenDocumentStore.Close(session, fileInB);
            }
        }
        finally
        {
            OpenDocumentStore.Close(session, fileInA);
        }
    }

    /// <summary>
    /// A keystroke moves the file being typed in and the projects that consume it. Nothing else.
    /// </summary>
    /// <remarks>
    /// The same overlay rebuild that made an open re-stamp everything did it on every
    /// <c>didChange</c> too — so typing one character in one file invalidated the cached analyzer
    /// run and the pull-diagnostics result id of every other open file in the window, on every
    /// keystroke. ProjectB references ProjectA, so an edit in B genuinely reaches nothing in A:
    /// A's document text and its project's dependent semantic version both have to hold still,
    /// while B's own document is expected to move — the edit still has to be visible.
    /// </remarks>
    [Fact]
    public async Task TypingInOneBufferLeavesUnrelatedOpenDocumentsAlone()
    {
        string session = Guid.NewGuid().ToString("N");
        string fileInA = FixturePaths.MultiProjectAClassFile;
        string fileInB = FixturePaths.MultiProjectBClassFile;

        await WorkspaceService.GetOrOpenProjectAsync(FixturePaths.MultiProjectAFile);
        await WorkspaceService.GetOrOpenProjectAsync(FixturePaths.MultiProjectBFile);

        string textOfB = await File.ReadAllTextAsync(fileInB);
        OpenDocumentStore.Open(
            session, fileInA, SourceText.From(await File.ReadAllTextAsync(fileInA)), version: 1);
        OpenDocumentStore.Open(session, fileInB, SourceText.From(textOfB), version: 1);
        try
        {
            var (beforeA, beforeTextA) = await SemanticAndTextVersionAsync(FixturePaths.MultiProjectAFile, fileInA);
            var (_, beforeTextB) = await SemanticAndTextVersionAsync(FixturePaths.MultiProjectBFile, fileInB);

            // The gesture: one character typed into B.
            OpenDocumentStore.Change(
                fileInB, version: 2, _ => SourceText.From(textOfB + "\n// keystroke\n"));

            var (afterA, afterTextA) = await SemanticAndTextVersionAsync(FixturePaths.MultiProjectAFile, fileInA);
            var (_, afterTextB) = await SemanticAndTextVersionAsync(FixturePaths.MultiProjectBFile, fileInB);

            Assert.Equal(beforeTextA, afterTextA);
            Assert.Equal(beforeA, afterA);

            // The other half: the edit is still applied, so the guard cannot be passing by
            // refusing to overlay anything at all.
            Assert.NotEqual(beforeTextB, afterTextB);
            var documentB = await GetDocumentAsync(FixturePaths.MultiProjectBFile, fileInB);
            Assert.Contains("// keystroke", (await documentB.GetTextAsync()).ToString());
        }
        finally
        {
            OpenDocumentStore.Close(session, fileInB);
            OpenDocumentStore.Close(session, fileInA);
        }
    }

    /// <summary>
    /// Following F12 into a project that was not loaded yet must not re-stamp the file you came
    /// from.
    /// </summary>
    /// <remarks>
    /// Navigating into an unloaded project adds it to the live workspace, which moves
    /// <c>CurrentSolution</c>. The project set legitimately changed and the editor is told so — but
    /// the documents that were already open did not change, and the buffer overlay must carry them
    /// across the add rather than re-applying them onto the new base and giving each a new version.
    /// </remarks>
    [Fact]
    public async Task LoadingAnotherProjectLeavesOpenDocumentsInLoadedProjectsAlone()
    {
        string session = Guid.NewGuid().ToString("N");
        string fileInA = FixturePaths.MultiProjectAClassFile;

        // Only ProjectA loaded, the way it is before you navigate anywhere.
        await WorkspaceService.EvictProjectForTests(FixturePaths.MultiProjectAFile);
        await WorkspaceService.EvictProjectForTests(FixturePaths.MultiProjectBFile);
        await WorkspaceService.GetOrOpenProjectAsync(FixturePaths.MultiProjectAFile);

        OpenDocumentStore.Open(
            session, fileInA, SourceText.From(await File.ReadAllTextAsync(fileInA)), version: 1);
        try
        {
            // What the LSP session's buffer bridge does on didOpen, awaited so the test does not
            // race the fire-and-forget reconcile the server schedules.
            await WorkspaceService.ReconcileOpenBufferAsync(fileInA);

            var (before, beforeText) = await SemanticAndTextVersionAsync(FixturePaths.MultiProjectAFile, fileInA);

            // The gesture: a second project joins the live workspace.
            await WorkspaceService.GetOrOpenProjectAsync(FixturePaths.MultiProjectBFile);

            var (after, afterText) = await SemanticAndTextVersionAsync(FixturePaths.MultiProjectAFile, fileInA);

            Assert.Equal(beforeText, afterText);
            Assert.Equal(before, after);
        }
        finally
        {
            OpenDocumentStore.Close(session, fileInA);
        }
    }

    /// <summary>
    /// Opening a file whose buffer matches disk changes nothing about the project.
    /// </summary>
    /// <remarks>
    /// This is the reported symptom in its most direct form. A buffer arrives as a fresh
    /// <see cref="SourceText"/> — didOpen builds one from the notification's text — so it is never
    /// the instance the workspace holds. Comparing by reference therefore re-applied it on every
    /// open, which re-stamped the document, moved its project's dependent semantic version, and
    /// missed the analyzer cache for every file in that project and every project depending on it.
    /// Open a file, watch every warning in the window go out.
    /// </remarks>
    [Fact]
    public async Task OpeningAFileUnchangedFromDiskLeavesItsProjectAlone()
    {
        string session = Guid.NewGuid().ToString("N");
        string file = FixturePaths.MultiProjectAClassFile;

        await WorkspaceService.EvictProjectForTests(FixturePaths.MultiProjectAFile);
        await WorkspaceService.GetOrOpenProjectAsync(FixturePaths.MultiProjectAFile);

        var (before, beforeText) = await SemanticAndTextVersionAsync(FixturePaths.MultiProjectAFile, file);

        // Exactly what the editor sends on didOpen: the file's text, in a new SourceText.
        OpenDocumentStore.Open(
            session, file, SourceText.From(await File.ReadAllTextAsync(file)), version: 1);
        try
        {
            await WorkspaceService.ReconcileOpenBufferAsync(file);

            var (after, afterText) = await SemanticAndTextVersionAsync(FixturePaths.MultiProjectAFile, file);

            Assert.Equal(beforeText, afterText);
            Assert.Equal(before, after);
        }
        finally
        {
            OpenDocumentStore.Close(session, file);
        }
    }

    /// <summary>
    /// Closing a file you never edited changes nothing either.
    /// </summary>
    /// <remarks>
    /// The close path reverted the document to its file loader unconditionally, so closing a tab
    /// re-stamped it and invalidated the whole project's analysis — for a buffer that had always
    /// matched what was on disk.
    /// </remarks>
    [Fact]
    public async Task ClosingAnUneditedFileLeavesItsProjectAlone()
    {
        string session = Guid.NewGuid().ToString("N");
        string file = FixturePaths.MultiProjectAClassFile;

        await WorkspaceService.EvictProjectForTests(FixturePaths.MultiProjectAFile);
        await WorkspaceService.GetOrOpenProjectAsync(FixturePaths.MultiProjectAFile);

        OpenDocumentStore.Open(
            session, file, SourceText.From(await File.ReadAllTextAsync(file)), version: 1);
        await WorkspaceService.ReconcileOpenBufferAsync(file);

        var (before, beforeText) = await SemanticAndTextVersionAsync(FixturePaths.MultiProjectAFile, file);

        OpenDocumentStore.Close(session, file);
        await WorkspaceService.ReconcileOpenBufferAsync(file);

        var (after, afterText) = await SemanticAndTextVersionAsync(FixturePaths.MultiProjectAFile, file);

        Assert.Equal(beforeText, afterText);
        Assert.Equal(before, after);
    }

    private static async Task<Document> GetDocumentAsync(string projectPath, string filePath)
    {
        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
            projectPath, targetFilePath: filePath, cancellationToken: default);
        var document = WorkspaceService.FindDocumentInProject(project, filePath);
        Assert.NotNull(document);
        return document!;
    }

    private static async Task<(VersionStamp Semantic, VersionStamp Text)> SemanticAndTextVersionAsync(
        string projectPath, string filePath)
    {
        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
            projectPath, targetFilePath: filePath, cancellationToken: default);
        var document = WorkspaceService.FindDocumentInProject(project, filePath);
        Assert.NotNull(document);

        return (await project.GetDependentSemanticVersionAsync(), await document!.GetTextVersionAsync());
    }

    private static async Task<Microsoft.CodeAnalysis.Compilation?> GetCompilationAsync(string projectPath)
    {
        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
            projectPath, cancellationToken: default);
        return await project.GetCompilationAsync();
    }

    private static async Task<string> GetSnapshotTextAsync(string filePath)
    {
        var projectPath = await WorkspaceService.FindContainingProjectAsync(filePath, default);
        Assert.NotNull(projectPath);
        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
            projectPath!, targetFilePath: filePath, cancellationToken: default);
        var document = WorkspaceService.FindDocumentInProject(project, filePath);
        Assert.NotNull(document);
        return (await document!.GetTextAsync()).ToString();
    }
}

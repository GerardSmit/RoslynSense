using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Collections;
using Microsoft.CodeAnalysis.Completion.Providers;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Completion;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;
using CompletionItem = RoslynMCP.Lsp.Protocol.CompletionItem;
using RoslynCompletionOptions = Microsoft.CodeAnalysis.Completion.CompletionOptions;

namespace RoslynMCP.Tests;

/// <summary>
/// The import-completion index and who is allowed to build it. Completion must serve whatever
/// entry is cached — never build one on the request thread — and the entry has to exist anyway,
/// which is <see cref="ImportCompletionWarmer"/>'s job.
/// </summary>
[Collection(SharedState.Name)]
public class ImportCompletionCacheTests
{
    /// <summary>How long a queued index build is given before the test calls it a failure. The
    /// build is a background compilation of the project plus a walk of its top-level types.</summary>
    private static readonly TimeSpan CacheWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A completion after an edit is answered from the cache, not from a rebuild.
    /// </summary>
    /// <remarks>
    /// <c>ForceExpandedCompletionIndexCreation</c> made every completion whose project had been
    /// edited rebuild that project's import-completion index before answering — the index is keyed
    /// by content checksum, so a single keystroke invalidated it and the next request paid a full
    /// background compilation plus a type walk on the request thread (~6s). The option is the
    /// assertion because it is the whole mechanism: the round trip below is fast either way on a
    /// fixture this size, so only the flag distinguishes the two behaviours.
    /// </remarks>
    [Fact]
    public async Task CompletionAfterARangedEditIsServedWithoutForcingAnIndexRebuild()
    {
        string path = FixturePaths.CalculatorFile;
        string session = $"cache-{Guid.NewGuid():N}";
        const string anchor = "return new Result(";
        string source = await File.ReadAllTextAsync(path);
        var text = SourceText.From(source);

        OpenDocumentStore.Open(session, path, text, version: 1);
        try
        {
            Assert.NotEmpty(await CompleteAtAsync(path, text, source.IndexOf(anchor, StringComparison.Ordinal) + anchor.Length));

            // The edit the editor sends: a range replaced in place, version advanced by one.
            int insertAt = source.IndexOf("public int Add", StringComparison.Ordinal);
            var edited = OpenDocumentStore.Change(path, version: 2,
                original => original.WithChanges(
                    new TextChange(new TextSpan(insertAt, 0), "public int Doubled(int a) => a + a;\r\n\r\n    ")));
            Assert.NotNull(edited);

            string editedSource = edited!.ToString();
            int caret = editedSource.IndexOf(anchor, StringComparison.Ordinal) + anchor.Length;
            Assert.NotEmpty(await CompleteAtAsync(path, edited, caret));
        }
        finally
        {
            OpenDocumentStore.Close(session, path);
        }

        var options = (RoslynCompletionOptions)typeof(CompletionHandler)
            .GetField("s_options", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

        Assert.False(options.ForceExpandedCompletionIndexCreation);
        Assert.True(options.UpdateImportCompletionCacheInBackground);
        Assert.True(options.ShowItemsFromUnimportedNamespaces);
    }

    /// <summary>
    /// A project whose index has never been built offers no unimported types until it is built.
    /// </summary>
    /// <remarks>
    /// This is the gap that forcing the rebuild used to paper over, and the reason the warmer
    /// exists: unforced completion reads the cache and does not create it, so a freshly loaded
    /// project silently omits every import-completion item. Evicting the project gives it a new
    /// <see cref="ProjectId"/>, which is the cache key — that is how the "no entry yet" state is
    /// reached deterministically rather than by clearing Roslyn's table.
    /// </remarks>
    [Fact]
    public async Task UnimportedTypesAppearOnlyOnceTheIndexIsBuilt()
    {
        string path = FixturePaths.MultiProjectBClassFile;
        string session = $"import-{Guid.NewGuid():N}";

        // A buffer with no using directive at all: ProjectA.Greeter can only reach the list
        // through import completion.
        const string source = """
            namespace ProjectB;

            public static class ImportProbe
            {
                public static string Probe()
                {
                    return Greet
                }
            }
            """;
        var text = SourceText.From(source);
        int caret = source.IndexOf("return Greet", StringComparison.Ordinal) + "return Greet".Length;

        await WorkspaceService.EvictProjectForTests(FixturePaths.MultiProjectBFile);
        await WorkspaceService.EvictProjectForTests(FixturePaths.MultiProjectAFile);

        OpenDocumentStore.Open(session, path, text, version: 1);
        try
        {
            var document = await LspDocumentResolver.ResolveAsync(path, default);
            Assert.NotNull(document);
            var project = document!.Project;

            // The freshly loaded project has no entry, so the item is genuinely absent first.
            Assert.False(AbstractTypeImportCompletionService.s_projectItemsCache.TryGetValue(project.Id, out _));
            Assert.DoesNotContain(await CompleteAtAsync(path, text, caret), IsUnimportedGreeter);

            // The deterministic, awaitable form of what the warmer queues. Warming the project
            // walks its references too, so ProjectA's entry is built with it.
            await AbstractTypeImportCompletionService.BatchUpdateCacheAsync(
                ImmutableSegmentedList.Create(project), default);
            await ExtensionMemberImportCompletionHelper.SymbolComputer.UpdateCacheAsync(project, default);

            Assert.True(AbstractTypeImportCompletionService.s_projectItemsCache.TryGetValue(project.Id, out _));
            Assert.Contains(await CompleteAtAsync(path, text, caret), IsUnimportedGreeter);
        }
        finally
        {
            OpenDocumentStore.Close(session, path);
        }

        // The namespace in Detail is what tells the editor committing will add a using — an item
        // named Greeter without it would be an ordinary in-scope type, not an import item.
        static bool IsUnimportedGreeter(CompletionItem item) =>
            item.Label == "Greeter" && item.Detail == "ProjectA";
    }

    /// <summary>
    /// didOpen's warm-up reaches Roslyn's queue and the queue produces an entry.
    /// </summary>
    /// <remarks>
    /// <see cref="ImportCompletionWarmer.LastScheduled"/> only says the work was queued, so the
    /// entry itself is polled for: the build runs on Roslyn's batching queue, off any thread this
    /// test controls.
    /// </remarks>
    [Fact]
    public async Task SchedulingAWarmUpBuildsTheProjectsIndexEntry()
    {
        string path = FixturePaths.MultiProjectAClassFile;
        string session = $"warm-{Guid.NewGuid():N}";

        await WorkspaceService.EvictProjectForTests(FixturePaths.MultiProjectAFile);

        OpenDocumentStore.Open(
            session, path, SourceText.From(await File.ReadAllTextAsync(path)), version: 1);
        try
        {
            var document = await LspDocumentResolver.ResolveAsync(path, default);
            Assert.NotNull(document);
            var projectId = document!.Project.Id;
            Assert.False(AbstractTypeImportCompletionService.s_projectItemsCache.TryGetValue(projectId, out _));

            // What didOpen does.
            ImportCompletionWarmer.Schedule(path, immediate: true);
            await ImportCompletionWarmer.LastScheduled;

            Assert.True(
                await PollAsync(() =>
                    AbstractTypeImportCompletionService.s_projectItemsCache.TryGetValue(projectId, out _)),
                $"no import-completion cache entry appeared within {CacheWait.TotalSeconds:0}s");
        }
        finally
        {
            OpenDocumentStore.Close(session, path);
        }
    }

    private static async Task<bool> PollAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + CacheWait;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(50);
        }
        return condition();
    }

    /// <summary>
    /// One completion request whose expanded pass is allowed to finish inside it. Import items
    /// arrive on a background pass that a live request merges only if it lands inside the grace
    /// window; what these tests assert is whether the index has anything to offer at all, so the
    /// window is widened out of the picture.
    /// </summary>
    private static async Task<CompletionItem[]> CompleteAtAsync(string path, SourceText text, int offset)
    {
        var position = text.Lines.GetLinePosition(offset);
        var previousGrace = ExpandedCompletionPass.GraceWindow;
        ExpandedCompletionPass.GraceWindow = TimeSpan.FromSeconds(30);
        try
        {
            var list = await CompletionHandler.CompletionAsync(
                new CompletionParams(
                    new TextDocumentIdentifier(LspConverters.PathToUri(path)),
                    new Position(position.Line, position.Character)),
                new LspResolveCache(),
                default);
            return list.Items;
        }
        finally
        {
            ExpandedCompletionPass.GraceWindow = previousGrace;
        }
    }
}

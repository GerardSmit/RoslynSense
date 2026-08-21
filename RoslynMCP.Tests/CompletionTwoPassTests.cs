using Microsoft.CodeAnalysis.Collections;
using Microsoft.CodeAnalysis.Completion.Providers;
using Microsoft.CodeAnalysis.Tags;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Completion;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;
using CompletionList = RoslynMCP.Lsp.Protocol.CompletionList;
using RoslynItem = Microsoft.CodeAnalysis.Completion.CompletionItem;

namespace RoslynMCP.Tests;

/// <summary>
/// Completion's two passes: the non-expanded providers answer the request, the import-completion
/// providers run behind it and merge into whichever request they are ready for. What is asserted
/// here is the seam between them — that the first list does not wait, that the second one gets the
/// items the first could not, that an edit throws the memoized pass away, and that a display text
/// offered by both passes appears once.
/// </summary>
[Collection(SharedState.Name)]
public class CompletionTwoPassTests
{
    /// <summary>
    /// A prefix both passes can answer: "S" hits string's own members, and — through import
    /// completion only — the ShoutRanking extension in the unimported SampleProject.Ranking.
    /// </summary>
    private const string Source = """
        namespace SampleProject;

        public class TwoPassSample
        {
            public string Compute(string value)
            {
                return value.S
            }
        }
        """;

    private const string Anchor = "return value.S";
    private const string ExpandedOnlyItem = "ShoutRanking";

    [Fact]
    public async Task TheFirstRequestAnswersWithoutWaitingForTheExpandedPass()
    {
        await WarmImportIndexesAsync();
        var gate = HoldTheExpandedPass();
        string session = Session();
        var text = SourceText.From(Source);

        OpenDocumentStore.Open(session, FixturePaths.CalculatorFile, text, version: 1);
        try
        {
            var list = await CompleteAsync(text);

            // In-scope members are there; the pass that would add the unimported extension is
            // still blocked on the gate, so its items are simply not in this response.
            Assert.NotEmpty(list.Items);
            Assert.Contains(list.Items, i => i.Label == "StartsWith");
            Assert.DoesNotContain(list.Items, i => i.Label == ExpandedOnlyItem);

            Assert.NotNull(ExpandedCompletionPass.Pending);
            Assert.False(ExpandedCompletionPass.Pending!.IsCompleted);
        }
        finally
        {
            await ReleaseAsync(gate, session);
        }
    }

    [Fact]
    public async Task TheNextRequestAtTheSamePositionMergesTheFinishedExpandedPass()
    {
        await WarmImportIndexesAsync();
        var gate = HoldTheExpandedPass();
        string session = Session();
        var text = SourceText.From(Source);

        OpenDocumentStore.Open(session, FixturePaths.CalculatorFile, text, version: 1);
        try
        {
            var first = await CompleteAsync(text);
            Assert.DoesNotContain(first.Items, i => i.Label == ExpandedOnlyItem);

            // isIncomplete is what makes the client come back at this position; here that
            // re-query is issued by hand, once the pass it will merge has finished.
            Assert.True(first.IsIncomplete);
            var pending = ExpandedCompletionPass.Pending;
            Assert.NotNull(pending);
            gate.SetResult();
            await pending!;

            var second = await CompleteAsync(text);

            // Once, not twice: the merge is also where cross-pass duplicates are collapsed.
            Assert.Single(second.Items, i => i.Label == ExpandedOnlyItem);
            Assert.Contains(second.Items, i => i.Label == "StartsWith");
        }
        finally
        {
            await ReleaseAsync(gate, session);
        }
    }

    [Fact]
    public async Task EditingTheBufferThrowsTheMemoizedPassAway()
    {
        await WarmImportIndexesAsync();
        var gate = HoldTheExpandedPass();
        string session = Session();
        var text = SourceText.From(Source);

        OpenDocumentStore.Open(session, FixturePaths.CalculatorFile, text, version: 1);
        try
        {
            await CompleteAsync(text);
            var before = ExpandedCompletionPass.Pending;
            var beforeKey = ExpandedCompletionPass.PendingKey;
            Assert.NotNull(before);

            // An edit past the caret: the position the pass is keyed to does not move, so the
            // checksum is the only part of the key that can invalidate it.
            var edited = OpenDocumentStore.Change(FixturePaths.CalculatorFile, version: 2,
                original => original.WithChanges(
                    new TextChange(new TextSpan(original.Length, 0), "\r\n// two-pass edit\r\n")));
            Assert.NotNull(edited);

            await CompleteAsync(edited!);
            var after = ExpandedCompletionPass.Pending;
            var afterKey = ExpandedCompletionPass.PendingKey;

            Assert.NotSame(before, after);
            Assert.Equal(beforeKey.Document, afterKey.Document);
            Assert.Equal(beforeKey.SpanStart, afterKey.SpanStart);
            Assert.NotEqual(beforeKey.Checksum, afterKey.Checksum);
        }
        finally
        {
            await ReleaseAsync(gate, session);
        }
    }

    [Fact]
    public void MergingKeepsTheInScopeItemWhenBothPassesOfferTheSameName()
    {
        var inScope = new[]
        {
            Item("List", "List"),
            Item("Compute", "Compute"),
        };
        var expanded = new[]
        {
            Unimported(Item("List", "List")),        // the same name, from a namespace not imported
            Unimported(Item("List", "List", "<>")),  // a different display text — the suffix says so
            Unimported(Item("Greeter", "Greeter")),
        };

        var merged = ExpandedCompletionPass.Merge(inScope, expanded);

        // The expanded "List" is gone; "List<>" is a different display text and stays.
        Assert.Equal(
            ["List", "Compute", "List<>", "Greeter"],
            merged.Select(i => i.DisplayText + i.DisplayTextSuffix).ToArray());

        // The kept "List" is the in-scope instance, whose commit inserts nothing but the name.
        Assert.Same(inScope[0], merged[0]);
        Assert.DoesNotContain(merged, i => ReferenceEquals(i, expanded[0]));
    }

    // --- helpers ---

    private static string Session() => $"twopass-{Guid.NewGuid():N}";

    private static RoslynItem Item(string displayText, string sortText, string? suffix = null) =>
        RoslynItem.Create(displayText, displayTextSuffix: suffix, sortText: sortText,
            tags: [WellKnownTags.Method]);

    /// <summary>What Roslyn marks an import-completion item with.</summary>
    private static RoslynItem Unimported(RoslynItem item)
    {
        item.Flags |= Microsoft.CodeAnalysis.Completion.CompletionItemFlags.Expanded;
        return item;
    }

    /// <summary>
    /// Blocks the expanded pass and shrinks the grace window to nothing, so that "the expanded
    /// items did not make it into this response" is a fact rather than a race.
    /// </summary>
    private static TaskCompletionSource HoldTheExpandedPass()
    {
        ExpandedCompletionPass.Reset();
        ExpandedCompletionPass.GraceWindow = TimeSpan.Zero;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ExpandedCompletionPass.Gate = gate.Task;
        return gate;
    }

    /// <summary>Lets every held pass run to completion before the fixture goes away, and puts the
    /// process-wide seams back the way production has them.</summary>
    private static async Task ReleaseAsync(TaskCompletionSource gate, string session)
    {
        gate.TrySetResult();
        var pending = ExpandedCompletionPass.Pending;
        if (pending is not null)
            await pending;

        ExpandedCompletionPass.Gate = Task.CompletedTask;
        ExpandedCompletionPass.GraceWindow = TimeSpan.FromMilliseconds(50);
        ExpandedCompletionPass.Reset();

        OpenDocumentStore.Close(session, FixturePaths.CalculatorFile);
        // The close-reconcile runs on a background task; a later test resolving this file against
        // its on-disk text must not race the overlay still being peeled off.
        await WorkspaceService.ReconcileOpenBufferAsync(FixturePaths.CalculatorFile);
    }

    private static async Task<CompletionList> CompleteAsync(SourceText text)
    {
        string source = text.ToString();
        int offset = source.IndexOf(Anchor, StringComparison.Ordinal) + Anchor.Length;
        var position = text.Lines.GetLinePosition(offset);

        return await CompletionHandler.CompletionAsync(
            new CompletionParams(
                new TextDocumentIdentifier(LspConverters.PathToUri(FixturePaths.CalculatorFile)),
                new Position(position.Line, position.Character)),
            new LspResolveCache(),
            default);
    }

    /// <summary>The deterministic, awaitable form of what ImportCompletionWarmer queues: the
    /// expanded pass serves from these indexes and builds none of them itself.</summary>
    private static async Task WarmImportIndexesAsync()
    {
        var document = await LspDocumentResolver.ResolveAsync(FixturePaths.CalculatorFile, default);
        Assert.NotNull(document);

        await AbstractTypeImportCompletionService.BatchUpdateCacheAsync(
            ImmutableSegmentedList.Create(document!.Project), default);
        await ExtensionMemberImportCompletionHelper.SymbolComputer.UpdateCacheAsync(
            document.Project, default);
    }
}

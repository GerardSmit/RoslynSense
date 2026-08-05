using RoslynMCP.Languages;
using RoslynMCP.Languages.Proto;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;
using LspRange = RoslynMCP.Lsp.Protocol.Range;

namespace RoslynMCP.Tests;

/// <summary>
/// The shared code-lens memo: it must answer the same as resolving from scratch, and must stop
/// answering the moment the state its answers were computed from changes.
/// </summary>
/// <remarks>
/// <para>
/// Keeping resolved lenses is the difference between a scroll costing a solution-wide symbol search
/// and costing a dictionary lookup. It is also the one change in that area that could put a stale
/// number in the gutter, and a wrong count is worse than a slow one — so invalidation is pinned
/// rather than assumed.
/// </para>
/// <para>
/// The contract is exercised against a fake pack rather than against a real one, and that is the
/// point rather than a shortcut: the memo is meant to serve every pack, so what is worth pinning is
/// what it promises <em>whatever</em> the pack is. A pack's own generation — whether it really does
/// change when that language's inputs change — belongs to that pack's tests. A fake also makes
/// "recomputed" observable, which against a real pack could only be inferred from timing.
/// </para>
/// </remarks>
public class CodeLensResolveMemoTests
{
    [Fact]
    public async Task AnAnswerIsKeptWhileTheGenerationHoldsAndDroppedWhenItMoves()
    {
        var pack = new FakeLensPack { Generation = "first" };
        var lens = LensAt(0, "references");

        Assert.Equal("1", await ResolveTitleAsync(pack, lens));

        // Same generation: served without asking the pack again.
        Assert.Equal("1", await ResolveTitleAsync(pack, lens));
        Assert.Equal("1", await ResolveTitleAsync(pack, lens));
        Assert.Equal(1, pack.Resolves);

        // A different slot in the same file is its own question.
        Assert.Equal("2", await ResolveTitleAsync(pack, LensAt(7, "references")));
        Assert.Equal("3", await ResolveTitleAsync(pack, LensAt(0, "implementations")));
        Assert.Equal(3, pack.Resolves);

        // The generation moving is what a keystroke, a rebuild or a project load looks like from
        // here. Everything kept for that file has to go, not only the lens that changed.
        pack.Generation = "second";

        Assert.Equal("4", await ResolveTitleAsync(pack, lens));
        Assert.Equal("5", await ResolveTitleAsync(pack, LensAt(7, "references")));
        Assert.Equal(5, pack.Resolves);

        // And it holds again at the new generation rather than recomputing forever.
        Assert.Equal("4", await ResolveTitleAsync(pack, lens));
        Assert.Equal(5, pack.Resolves);
    }

    /// <summary>
    /// The range on the answer is the one the client sent, not the one that was kept.
    /// </summary>
    /// <remarks>
    /// A client is entitled to adjust a lens's range — text moved under it since the list was
    /// produced — and resolving is defined as filling in the command. Returning the remembered lens
    /// wholesale would drag a stale range back with the fresh command and draw the gutter entry at
    /// the wrong line.
    /// </remarks>
    [Fact]
    public async Task TheRangeComesFromTheLensTheClientSent()
    {
        var pack = new FakeLensPack { Generation = "only" };

        _ = await CodeLensResolveMemo.ResolveAsync(pack, LensAt(0, "references"), default);

        var moved = LensAt(0, "references") with
        {
            Range = new LspRange(new Position(99, 1), new Position(99, 9)),
        };

        var resolved = await CodeLensResolveMemo.ResolveAsync(pack, moved, default);

        Assert.Equal(99, resolved.Range.Start.Line);
        Assert.Equal("1", resolved.Command?.Title);
        Assert.Equal(1, pack.Resolves);
    }

    /// <summary>
    /// A pack that does not describe its state resolves uncached rather than being refused.
    /// </summary>
    /// <remarks>
    /// This is what makes the memo safe to put in the routing layer for everyone: adding a pack
    /// costs nothing until it opts in, and the packs that exist today did not have to change with
    /// it. The same applies within an opted-in pack when it cannot describe a file yet — no view,
    /// nothing built — which is the null case below.
    /// </remarks>
    [Fact]
    public async Task APackThatCannotDescribeItsStateIsPassedStraightThrough()
    {
        var never = new PlainLensPack();
        Assert.Equal("1", await ResolveTitleAsync(never, LensAt(0, "references")));
        Assert.Equal("2", await ResolveTitleAsync(never, LensAt(0, "references")));
        Assert.Equal(2, never.Resolves);

        var notYet = new FakeLensPack { Generation = null };
        Assert.Equal("1", await ResolveTitleAsync(notYet, LensAt(0, "references")));
        Assert.Equal("2", await ResolveTitleAsync(notYet, LensAt(0, "references")));
        Assert.Equal(2, notYet.Resolves);
    }

    /// <summary>
    /// The proto pack really does opt in, and going through the memo gives what the pack gives.
    /// </summary>
    /// <remarks>
    /// Read-only on purpose. An earlier version of this test edited the fixture <c>.proto</c> to
    /// prove invalidation and broke five unrelated tests in other classes, because xUnit runs
    /// classes in parallel and that file is shared — the sort of failure that reads as a bug in the
    /// feature under test rather than in the test. Invalidation is covered above, where it costs
    /// nobody anything.
    /// </remarks>
    [Fact]
    public async Task TheProtoPackOptsInAndAgreesWithItsOwnUncachedAnswer()
    {
        string proto = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "ProtoSolution", "Contracts", "widgets", "widgets.proto"));
        Assert.True(File.Exists(proto), $"fixture missing: {proto}");

        var pack = new ProtoLanguage(new MarkdownFormatter());
        string uri = LspConverters.PathToUri(proto);

        Assert.IsAssignableFrom<ILanguageCodeLensGeneration>(pack);
        Assert.NotNull(await ((ILanguageCodeLensGeneration)pack).LensGenerationAsync(uri, default));

        var lenses = await pack.CodeLensAsync(new CodeLensParams(new TextDocumentIdentifier(uri)), default);
        Assert.NotEmpty(lenses);

        foreach (var lens in lenses)
        {
            var direct = await pack.ResolveCodeLensAsync(lens, default);
            var kept = await CodeLensResolveMemo.ResolveAsync(pack, lens, default);
            var served = await CodeLensResolveMemo.ResolveAsync(pack, lens, default);

            Assert.Equal(direct.Command?.Title, kept.Command?.Title);
            Assert.Equal(direct.Command?.Title, served.Command?.Title);
        }
    }

    private static CodeLens LensAt(int line, string kind) =>
        new(new LspRange(new Position(line, 0), new Position(line, 4)), Command: null)
        {
            // A URI of its own per test class, so entries cannot collide with another test's file
            // in the process-wide memo.
            Data = new CodeLensData("file:///code-lens-memo-tests/probe.fake", line, 0, kind),
        };

    private static async Task<string?> ResolveTitleAsync(ILanguageCodeLensProvider pack, CodeLens lens) =>
        (await CodeLensResolveMemo.ResolveAsync(pack, lens, default)).Command?.Title;

    /// <summary>A pack whose state is whatever the test says it is.</summary>
    private sealed class FakeLensPack : ILanguageCodeLensProvider, ILanguageCodeLensGeneration
    {
        public int Resolves;
        public string? Generation;

        public ValueTask<object?> LensGenerationAsync(string uri, CancellationToken ct) =>
            ValueTask.FromResult((object?)Generation);

        public Task<CodeLens[]> CodeLensAsync(CodeLensParams p, CancellationToken ct) =>
            Task.FromResult<CodeLens[]>([]);

        public Task<CodeLens> ResolveCodeLensAsync(CodeLens lens, CancellationToken ct) =>
            Task.FromResult(lens with { Command = new Command($"{++Resolves}", "noop", []) });
    }

    /// <summary>A pack that has not opted in at all.</summary>
    private sealed class PlainLensPack : ILanguageCodeLensProvider
    {
        public int Resolves;

        public Task<CodeLens[]> CodeLensAsync(CodeLensParams p, CancellationToken ct) =>
            Task.FromResult<CodeLens[]>([]);

        public Task<CodeLens> ResolveCodeLensAsync(CodeLens lens, CancellationToken ct) =>
            Task.FromResult(lens with { Command = new Command($"{++Resolves}", "noop", []) });
    }
}

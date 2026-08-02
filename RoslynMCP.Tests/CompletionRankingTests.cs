using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Tags;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Completion;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;
using CompletionItem = RoslynMCP.Lsp.Protocol.CompletionItem;
using RoslynItem = Microsoft.CodeAnalysis.Completion.CompletionItem;

namespace RoslynMCP.Tests;

/// <summary>
/// The completion list's order. Roslyn decides what is in scope; these cover the layer that
/// decides what comes first — CamelHumps matching, the relevance bit word, the typo tier and the
/// deliberately weak usage statistics.
/// </summary>
[Collection(SharedState.Name)]
public class CompletionRankingTests
{
    // --- matcher ---

    [Theory]
    [InlineData("sb", "StringBuilder")]
    [InlineData("SB", "StringBuilder")]
    [InlineData("strbuilder", "StringBuilder")]
    [InlineData("stringbuilder", "StringBuilder")]
    [InlineData("build", "StringBuilder")]
    [InlineData("tolower", "ToLowerInvariant")]
    public void CamelHumpsMatchesWhatAnIdeUserExpects(string pattern, string candidate)
    {
        Assert.NotNull(new IdentifierMatcher(pattern).Match(candidate));
    }

    [Theory]
    [InlineData("xyz", "StringBuilder")]
    // "tri" is inside "String" but starts mid-word: a substring hit, not a completion hit.
    [InlineData("tri", "StringBuilder")]
    [InlineData("bs", "StringBuilder")]
    public void MidWordAndOutOfOrderHitsAreRejected(string pattern, string candidate)
    {
        Assert.Null(new IdentifierMatcher(pattern).Match(candidate));
    }

    [Fact]
    public void PrefixStyleRefusesToStartAtALaterHump()
    {
        Assert.Null(new IdentifierMatcher("builder", IdentifierMatchingStyle.BeginningOfIdentifier)
            .Match("StringBuilder"));
        Assert.NotNull(new IdentifierMatcher("builder").Match("StringBuilder"));
    }

    [Fact]
    public void ScoreOrdersExactAbovePrefixAboveCamelHumps()
    {
        var exact = new IdentifierMatcher("Count").Match("Count")!.Value.Score;
        var prefix = new IdentifierMatcher("Cou").Match("Count")!.Value.Score;
        var humps = new IdentifierMatcher("cc").Match("ContainsCount")!.Value.Score;

        Assert.True(exact > prefix);
        Assert.True(prefix > humps);
        Assert.True(exact.IsExactMatch());
        Assert.True(prefix.IsExactPrefixMatch());
    }

    [Fact]
    public void CaseTypoScoresBelowTheSameMatchWithCorrectCase()
    {
        var correct = new IdentifierMatcher("Count").Match("Count")!.Value.Score;
        var wrongCase = new IdentifierMatcher("count").Match("Count")!.Value.Score;

        Assert.True(correct > wrongCase);
        Assert.False(correct.HasTypos());
        Assert.False(wrongCase.HasTypos());
    }

    [Theory]
    [InlineData("srting", "String")]     // transposition
    [InlineData("stribg", "StringName")] // substitution
    public void LongEnoughPatternsSurviveOneTypo(string pattern, string candidate)
    {
        var match = new IdentifierMatcher(pattern).Match(candidate);

        Assert.NotNull(match);
        Assert.True(match!.Value.Score.HasTypos());
    }

    [Fact]
    public void ShortPatternsAreNotTypoCorrected()
    {
        // At four characters "srtn" is far likelier to be a different identifier than a typo.
        Assert.Null(new IdentifierMatcher("srtn").Match("String"));
    }

    // --- relevance ordering ---

    [Fact]
    public void LocalsOutrankMembersOutrankTypes()
    {
        var ranked = Rank("co", Item("count", WellKnownTags.Local),
            Item("Compute", WellKnownTags.Method),
            Item("Console", WellKnownTags.Class));

        Assert.Equal(["count", "Compute", "Console"], Labels(ranked));
    }

    [Fact]
    public void ObsoleteItemsSinkBelowTheirOwnKind()
    {
        var ranked = Rank("comp",
            Item("Compute", WellKnownTags.Method, WellKnownTags.Deprecated),
            Item("Compare", WellKnownTags.Method));

        Assert.Equal(["Compare", "Compute"], Labels(ranked));
    }

    [Fact]
    public void ObjectMembersSinkBelowTheTypesOwnMembers()
    {
        var semantics = CompletionSemanticContext.FromNames(new Dictionary<string, MemberProvenance>
        {
            ["ToString"] = MemberProvenance.Object,
            ["Total"] = MemberProvenance.CurrentType,
        });

        var ranked = Rank("to", semantics,
            Item("ToString", WellKnownTags.Method),
            Item("Total", WellKnownTags.Method));

        Assert.Equal(["Total", "ToString"], Labels(ranked));
    }

    [Fact]
    public void InheritedMembersSinkBelowTheTypesOwn()
    {
        var semantics = CompletionSemanticContext.FromNames(new Dictionary<string, MemberProvenance>
        {
            ["ComputeOwn"] = MemberProvenance.CurrentType,
            ["ComputeInherited"] = MemberProvenance.BaseType,
        });

        var ranked = Rank("comp", semantics,
            Item("ComputeInherited", WellKnownTags.Method),
            Item("ComputeOwn", WellKnownTags.Method));

        Assert.Equal(["ComputeOwn", "ComputeInherited"], Labels(ranked));
    }

    [Fact]
    public void TheNearestLocalOutranksItsPeers()
    {
        var semantics = CompletionSemanticContext.FromNames(
            new Dictionary<string, MemberProvenance>(), closestLocalName: "countB");

        var ranked = Rank("count", semantics,
            Item("countA", WellKnownTags.Local),
            Item("countB", WellKnownTags.Local));

        Assert.Equal(["countB", "countA"], Labels(ranked));
    }

    [Fact]
    public void UnimportedTypesSinkBelowImportedOnes()
    {
        var ranked = Rank("li",
            Unimported(Item("ListView", WellKnownTags.Class)),
            Item("List", WellKnownTags.Class));

        Assert.Equal(["List", "ListView"], Labels(ranked));
    }

    [Fact]
    public void ExpectedTypeMatchWinsOverKind()
    {
        // At equal match quality Roslyn's target-type signal outranks the element-kind band, so
        // the field that actually fits the expected type beats the local.
        var ranked = Rank("Sta",
            Item("State", WellKnownTags.Local),
            Item("Started", WellKnownTags.Field, WellKnownTags.TargetTypeMatch));

        Assert.Equal(["Started", "State"], Labels(ranked));
    }

    [Fact]
    public void MatchQualityOutranksEverythingBelowIt()
    {
        // Match band first: a same-case prefix hit on a type beats a wrong-case hit on a local,
        // however much higher locals normally rank.
        var ranked = Rank("Co",
            Item("count", WellKnownTags.Local),
            Item("Console", WellKnownTags.Class));

        Assert.Equal(["Console", "count"], Labels(ranked));
    }

    [Fact]
    public void ExactMatchBeatsEveryKindDifference()
    {
        var ranked = Rank("Console",
            Item("consoleWriter", WellKnownTags.Local),
            Item("Console", WellKnownTags.Class));

        Assert.Equal(["Console", "consoleWriter"], Labels(ranked));
    }

    [Fact]
    public void TypoMatchesDisappearAsSoonAsACleanMatchExists()
    {
        var withClean = Rank("srting", Item("String", WellKnownTags.Class), Item("Srting", WellKnownTags.Class));
        Assert.Equal(["Srting"], Labels(withClean));

        var typoOnly = Rank("srting", Item("String", WellKnownTags.Class));
        Assert.Equal(["String"], Labels(typoOnly));
    }

    [Fact]
    public void SortTextIsAscendingInTheOrderTheServerChose()
    {
        var ranked = Rank("co", Item("count", WellKnownTags.Local),
            Item("Compute", WellKnownTags.Method),
            Item("Console", WellKnownTags.Class));

        var sortTexts = ranked.Items.Select((entry, index) => entry.SortText(index)).ToArray();
        Assert.Equal(sortTexts.OrderBy(s => s, StringComparer.Ordinal), sortTexts);
    }

    // --- statistics ---

    [Fact]
    public void UsageReordersInsideATierButNeverAcrossOne()
    {
        CompletionStatistics.Reset();
        try
        {
            var compare = Item("Compare", WellKnownTags.Method);
            var compute = Item("Compute", WellKnownTags.Method);
            var count = Item("count", WellKnownTags.Local);

            Assert.Equal(["count", "Compare", "Compute"], Labels(Rank("co", count, compare, compute)));

            // Picking the method repeatedly promotes it inside the method tier only — the local
            // still wins, which is the property that keeps the list predictable.
            for (int i = 0; i < 5; i++)
                CompletionStatistics.Record("expression", CompletionStatistics.Identity(compute));

            Assert.Equal(["count", "Compute", "Compare"], Labels(Rank("co", count, compare, compute)));
        }
        finally
        {
            CompletionStatistics.Reset();
        }
    }

    [Fact]
    public void AcceptCommandRidesAlongAndFeedsStatistics()
    {
        CompletionStatistics.Reset();
        try
        {
            var item = Item("Compute", WellKnownTags.Method);
            string identity = CompletionStatistics.Identity(item);
            Assert.Equal(-1, CompletionStatistics.Score("expression", identity));

            CompletionStatistics.Record("expression", identity);

            Assert.True(CompletionStatistics.Score("expression", identity) > 0);
            Assert.Equal(-1, CompletionStatistics.Score("member:list", identity));
        }
        finally
        {
            CompletionStatistics.Reset();
        }
    }

    [Fact]
    public void StatisticsContextSeparatesMemberAccessFromOpenExpressions()
    {
        var text = SourceText.From("var x = builder.App");

        Assert.Equal("member:builder", CompletionRanker.ContextId(text, new TextSpan(16, 3)));
        Assert.Equal("expression", CompletionRanker.ContextId(text, new TextSpan(8, 7)));
    }

    // --- through the real handler ---

    [Fact]
    public async Task LocalVariableComesFirstThroughTheRequestPath()
    {
        string source = """
            namespace SampleProject;

            public class RankingSample
            {
                public int Total { get; set; }

                public int Compute(int totalCount)
                {
                    var totals = totalCount + 1;
                    return tot
                }
            }
            """;

        var items = await CompleteAsync(source, "return tot");

        // The local and the parameter take the first two slots; the property comes after them.
        Assert.Equal(["totalCount", "totals"], items.Take(2).Select(i => i.Label).Order().ToArray());
        Assert.True(
            Array.FindIndex(items, i => i.Label == "Total") > 1,
            $"property outranked the locals: {string.Join(", ", items.Take(5).Select(i => i.Label))}");
    }

    [Fact]
    public async Task ObjectMembersDoNotHeadTheListAfterADot()
    {
        string source = """
            namespace SampleProject;

            public class RankingSampleTwo
            {
                public int Compute(string value)
                {
                    return value.To
                }
            }
            """;

        var items = await CompleteAsync(source, "return value.To");
        int toString = Array.FindIndex(items, i => i.Label == "ToString");
        int toUpper = Array.FindIndex(items, i => i.Label == "ToUpper");

        Assert.True(toUpper >= 0 && toString >= 0, $"got: {string.Join(", ", items.Take(10).Select(i => i.Label))}");
        Assert.True(toUpper < toString, "ToString outranked the string's own members");
    }

    [Fact]
    public async Task FilterTextStartsWithWhatWasTypedSoTheClientCannotReorderTheList()
    {
        string source = """
            namespace SampleProject;

            public class RankingSampleThree
            {
                public int Compute(string value)
                {
                    return value.To
                }
            }
            """;

        var items = await CompleteAsync(source, "return value.To");

        // Identical leading characters mean an identical client-side fuzzy score, which leaves
        // sortText — the server's ranking — as the only thing left to sort on. The rest of the
        // name stays in there so the item survives the next keystroke's in-flight request.
        Assert.All(items, i =>
        {
            Assert.StartsWith("To", i.FilterText, StringComparison.Ordinal);
            Assert.EndsWith(i.Label, i.FilterText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task FilterTextIsThePlainNameWhenNothingWasTypedYet()
    {
        string source = """
            namespace SampleProject;

            public class RankingSampleFour
            {
                public int Compute(string value)
                {
                    return value.
                }
            }
            """;

        var items = await CompleteAsync(source, "return value.");

        Assert.All(items, i => Assert.Equal(i.Label, i.FilterText));
    }

    [Fact]
    public async Task ExtensionMethodsFromUnimportedNamespacesAreOfferedAndBringTheirUsing()
    {
        string source = """
            namespace SampleProject;

            public class RankingSampleFive
            {
                public string Compute(string value)
                {
                    return value.Shout
                }
            }
            """;

        var (items, cache) = await CompleteWithCacheAsync(source, "return value.Shout");

        var shout = Array.Find(items, i => i.Label == "ShoutRanking");
        Assert.True(shout is not null,
            $"import completion missed the extension; got: {string.Join(", ", items.Take(10).Select(i => i.Label))}");
        Assert.Equal("SampleProject.Ranking", shout!.Detail);

        var resolved = await CompletionHandler.ResolveAsync(shout, cache, default);
        Assert.NotNull(resolved.AdditionalTextEdits);
        Assert.Contains(resolved.AdditionalTextEdits!, e => e.NewText.Contains("using SampleProject.Ranking", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstanceMethodsOutrankRealExtensionMethods()
    {
        string source = """
            using System.Linq;

            namespace SampleProject;

            public class RankingSampleSix
            {
                public int Compute(string value)
                {
                    return value.To
                }
            }
            """;

        var items = await CompleteAsync(source, "return value.To");
        int toUpper = Array.FindIndex(items, i => i.Label == "ToUpper");
        int toList = Array.FindIndex(items, i => i.Label == "ToList");

        // ToUpper is tagged ExtensionMethod by Roslyn (MemoryExtensions has an overload) but is
        // found on string itself, so it ranks with the instance methods; ToList is not.
        Assert.True(toUpper >= 0 && toList >= 0,
            $"got: {string.Join(", ", items.Take(10).Select(i => i.Label))}");
        Assert.True(toUpper < toList, "a real extension method outranked an instance method");
    }

    [Fact]
    public async Task TheNearestLocalWinsOverTheOnesDeclaredEarlier()
    {
        string source = """
            namespace SampleProject;

            public class RankingSampleSeven
            {
                public int Compute()
                {
                    var valueOne = 1;
                    var valueTwo = 2;
                    return val
                }
            }
            """;

        var items = await CompleteAsync(source, "return val");

        // Same kind, same match quality: proximity to the caret decides, not the alphabet.
        Assert.Equal("valueTwo", items[0].Label);
        Assert.Equal("valueOne", items[1].Label);
    }

    [Fact]
    public async Task MembersOfTheTypeItselfOutrankInheritedOnes()
    {
        string source = """
            namespace SampleProject;

            public class RankingBase
            {
                public int RankValueInherited() => 1;
            }

            public class RankingDerived : RankingBase
            {
                public int RankValueOwn() => 2;

                public int Compute()
                {
                    return RankValue
                }
            }
            """;

        var items = await CompleteAsync(source, "return RankValue");
        int own = Array.FindIndex(items, i => i.Label == "RankValueOwn");
        int inherited = Array.FindIndex(items, i => i.Label == "RankValueInherited");

        Assert.True(own >= 0 && inherited >= 0,
            $"got: {string.Join(", ", items.Take(10).Select(i => i.Label))}");
        Assert.True(own < inherited, "an inherited member outranked the type's own");
    }

    // --- helpers ---

    private static RankingResult Rank(string prefix, params RoslynItem[] items) =>
        CompletionRanker.Rank(items, prefix, "expression", limit: 100);

    private static RankingResult Rank(string prefix, CompletionSemanticContext semantics, params RoslynItem[] items) =>
        CompletionRanker.Rank(items, prefix, "expression", limit: 100, semantics);

    private static string[] Labels(RankingResult result) =>
        result.Items.Select(i => i.Item.DisplayText).ToArray();

    private static RoslynItem Item(string displayText, params string[] tags) =>
        RoslynItem.Create(displayText, tags: [.. tags], sortText: displayText);

    /// <summary>What Roslyn marks an import-completion item with.</summary>
    private static RoslynItem Unimported(RoslynItem item)
    {
        item.Flags |= CompletionItemFlags.Expanded;
        return item;
    }

    private static async Task<CompletionItem[]> CompleteAsync(string source, string anchor) =>
        (await CompleteWithCacheAsync(source, anchor)).Items;

    private static async Task<(CompletionItem[] Items, LspResolveCache Cache)> CompleteWithCacheAsync(
        string source, string anchor)
    {
        string path = FixturePaths.CalculatorFile;
        string sessionId = $"ranking-{Guid.NewGuid():N}";
        var text = SourceText.From(source);

        OpenDocumentStore.Open(sessionId, path, text, version: 1);
        try
        {
            int offset = source.IndexOf(anchor, StringComparison.Ordinal) + anchor.Length;
            var linePosition = text.Lines.GetLinePosition(offset);

            var cache = new LspResolveCache();
            var list = await CompletionHandler.CompletionAsync(
                new CompletionParams(
                    new TextDocumentIdentifier(LspConverters.PathToUri(path)),
                    new Position(linePosition.Line, linePosition.Character)),
                cache,
                default);

            Assert.NotEmpty(list.Items);
            return (list.Items, cache);
        }
        finally
        {
            OpenDocumentStore.Close(sessionId, path);
        }
    }
}

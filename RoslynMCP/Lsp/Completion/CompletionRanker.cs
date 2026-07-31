using Microsoft.CodeAnalysis.Text;
using RoslynItem = Microsoft.CodeAnalysis.Completion.CompletionItem;

namespace RoslynMCP.Lsp.Completion;

/// <summary>One item with everything the sort needs.</summary>
/// <param name="Relevance">Full 64-bit relevance word, match bits included.</param>
public readonly record struct RankedCompletion(
    RoslynItem Item,
    ulong Relevance,
    MatcherScore Score,
    int OriginalIndex)
{
    /// <summary>Lexicographically ascending in the client == descending relevance here.</summary>
    public string SortText(int rank) => CompletionRanker.SortText(Relevance, rank);
}

public sealed record RankingResult(IReadOnlyList<RankedCompletion> Items, bool Truncated);

/// <summary>
/// The per-keystroke half of completion: match every evaluated item against what the user typed,
/// stamp the match quality onto its relevance word, let usage statistics break one tie, and sort.
/// Cheap enough to redo on each request because it touches no semantic model — Roslyn's item list
/// is computed once and only re-ranked here.
/// </summary>
public static class CompletionRanker
{
    /// <summary>Statistics only reorder the first tiers; below that nobody is looking.</summary>
    private const int MaxStatisticalGroups = 20;

    public static RankingResult Rank(
        IReadOnlyList<RoslynItem> items,
        string prefix,
        string contextId,
        int limit,
        CompletionSemanticContext? semantics = null)
    {
        semantics ??= CompletionSemanticContext.Empty;
        var matcher = new IdentifierMatcher(prefix);
        var matched = new List<RankedCompletion>(items.Count);

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            string text = string.IsNullOrEmpty(item.FilterText) ? item.DisplayText : item.FilterText;
            if (matcher.Match(text) is not { } match)
                continue;

            matched.Add(new RankedCompletion(item, CompletionRelevance.Compute(item, semantics), match.Score, i));
        }

        // A typo-corrected hit is a guess. As soon as one hit needs no correction the guesses are
        // wrong by definition, so they all go. (Case is not a typo here — a wrong-case match
        // stays in the list, it just ranks below the right-case one.)
        if (matched.Exists(m => !m.Score.HasTypos()))
            matched.RemoveAll(m => m.Score.HasTypos());

        for (int i = 0; i < matched.Count; i++)
        {
            var m = matched[i];
            matched[i] = m with { Relevance = m.Relevance | MatchBits(m.Score) };
        }

        ApplyStatistics(matched, contextId);

        matched.Sort(Compare);

        bool truncated = matched.Count > limit;
        return new RankingResult(truncated ? matched.GetRange(0, limit) : matched, truncated);
    }

    /// <summary>
    /// Relevance descending, then Roslyn's own sort text, then evaluation order — the last two
    /// keep items inside one relevance tier in a stable, alphabetical-looking order.
    /// </summary>
    private static int Compare(RankedCompletion x, RankedCompletion y)
    {
        int byRelevance = y.Relevance.CompareTo(x.Relevance);
        if (byRelevance != 0)
            return byRelevance;

        int bySortText = string.CompareOrdinal(x.Item.SortText, y.Item.SortText);
        return bySortText != 0 ? bySortText : x.OriginalIndex.CompareTo(y.OriginalIndex);
    }

    private static ulong MatchBits(MatcherScore score)
    {
        bool caseMatch = (score & MatcherScore.FirstLetterHumpCaseMatch) != 0;

        if ((score & MatcherScore.ExactMatch) != 0)
            return (ulong)(caseMatch ? LookupItemRelevance.ExactMatch : LookupItemRelevance.ExactNoCaseMatch);

        if ((score & MatcherScore.ExactPrefixMatch) != 0)
            return (ulong)(caseMatch ? LookupItemRelevance.PrefixMatch : LookupItemRelevance.PrefixNoCaseMatch);

        if ((score & MatcherScore.AllHumpsMatch) != 0)
        {
            bool clean = caseMatch && (score & MatcherScore.NoCaseTypos) != 0;
            return (ulong)(clean
                ? LookupItemRelevance.CamelHumpsCaseMatch
                : LookupItemRelevance.CamelHumpsNoCaseMatch);
        }

        return 0;
    }

    /// <summary>
    /// Inside each relevance tier, the most-used item gets the <see cref="LookupItemRelevance.Statistical"/>
    /// bit — the lowest bit that can change an order, so learning never crosses a tier boundary.
    /// </summary>
    private static void ApplyStatistics(List<RankedCompletion> matched, string contextId)
    {
        if (matched.Count == 0)
            return;

        var order = new int[matched.Count];
        for (int i = 0; i < order.Length; i++)
            order[i] = i;

        ulong Tier(int index) => matched[index].Relevance & (ulong)LookupItemRelevance.AboveStatisticalMask;

        Array.Sort(order, (a, b) =>
        {
            int byTier = Tier(b).CompareTo(Tier(a));
            return byTier != 0 ? byTier : a.CompareTo(b);
        });

        int groups = 0;
        int groupStart = 0;
        while (groupStart < order.Length && groups < MaxStatisticalGroups)
        {
            ulong tier = Tier(order[groupStart]);
            int groupEnd = groupStart;
            int best = -1;
            long bestScore = 0;

            while (groupEnd < order.Length && Tier(order[groupEnd]) == tier)
            {
                int index = order[groupEnd];
                long score = CompletionStatistics.Score(contextId, CompletionStatistics.Identity(matched[index].Item));
                if (score > bestScore)
                {
                    bestScore = score;
                    best = index;
                }

                groupEnd++;
            }

            if (best >= 0)
            {
                var promoted = matched[best];
                matched[best] = promoted with
                {
                    Relevance = promoted.Relevance | (ulong)LookupItemRelevance.Statistical,
                };
            }

            groupStart = groupEnd;
            groups++;
        }
    }

    /// <summary>
    /// Client-visible sort key. Complemented so that ascending string order is descending
    /// relevance, with the rank appended so equal-relevance items keep the order decided here.
    /// </summary>
    public static string SortText(ulong relevance, int rank) => $"{~relevance:x16}{rank:x4}";

    /// <summary>
    /// Bucket that usage statistics are keyed by. Completion after a dot is a different world
    /// from completion in an open expression, and the qualifier text separates the buckets
    /// further — an approximation of ReSharper's per-provider context id that needs no semantics.
    /// </summary>
    public static string ContextId(SourceText text, TextSpan span)
    {
        int i = span.Start - 1;
        while (i >= 0 && (text[i] == ' ' || text[i] == '\t'))
            i--;

        if (i < 0 || text[i] != '.')
            return "expression";

        int end = i;
        i--;
        while (i >= 0 && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '.'))
            i--;

        string qualifier = text.ToString(TextSpan.FromBounds(i + 1, end));
        return qualifier.Length == 0 ? "member" : $"member:{qualifier}";
    }
}

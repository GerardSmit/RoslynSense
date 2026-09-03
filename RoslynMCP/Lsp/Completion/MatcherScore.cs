namespace RoslynMCP.Lsp.Completion;

/// <summary>
/// Quality of a single prefix-to-candidate match, as flags whose <em>bit order is the
/// ranking order</em>: comparing two scores as plain integers compares match quality, no
/// weights to tune. Modelled on ReSharper's matcher score.
/// </summary>
[Flags]
public enum MatcherScore
{
    None = 0,

    /// <summary>Pattern's first letter matched a hump with the same case ("SB" → "StringBuilder").</summary>
    FirstLetterHumpCaseMatch = 1 << 0,

    /// <summary>The matched humps are adjacent humps — no skipped word in between.</summary>
    NoGapsBetweenHumps = 1 << 1,

    /// <summary>All pattern characters landed on one contiguous run of the candidate.</summary>
    ExactMiddleMatch = 1 << 2,

    /// <summary>Every word the match touched was consumed whole.</summary>
    WholeWordsMatch = 1 << 3,

    /// <summary>The match starts at the candidate's first hump.</summary>
    FirstHumpMatch = 1 << 4,

    /// <summary>Every hump of the candidate was matched — the classic camel-hump hit.</summary>
    AllHumpsMatch = 1 << 5,

    /// <summary>Characters matched in pattern order (always set for a real alignment).</summary>
    CorrectOrder = 1 << 6,

    /// <summary>The candidate starts with the pattern.</summary>
    ExactPrefixMatch = 1 << 7,

    /// <summary>The candidate is the pattern.</summary>
    ExactMatch = 1 << 8,

    /// <summary>No character had to be case-corrected.</summary>
    NoCaseTypos = 1 << 9,

    /// <summary>No character had to be typo-corrected.</summary>
    NoTypos = 1 << 10,
}

public static class MatcherScoreExtensions
{
    public static bool HasTypos(this MatcherScore score) =>
        score != MatcherScore.None && (score & MatcherScore.NoTypos) == 0;

    public static bool IsExactMatch(this MatcherScore score) =>
        (score & (MatcherScore.NoTypos | MatcherScore.NoCaseTypos | MatcherScore.ExactMatch))
        == (MatcherScore.NoTypos | MatcherScore.NoCaseTypos | MatcherScore.ExactMatch);

    /// <summary>
    /// The pattern landed on one unbroken run of the candidate, and consumed every word it touched.
    /// </summary>
    /// <remarks>
    /// Weaker than an exact match and much stronger than an alignment: typing "ShopController"
    /// against <c>SomePrefixShopController</c> is someone naming a type by the part of it they
    /// remember, which is how people search for types whose prefix is a house convention. Without a
    /// way to say that, such a match was indistinguishable from any scattered camel-hump hit.
    /// </remarks>
    public static bool IsWholeWordMatch(this MatcherScore score) =>
        (score & (MatcherScore.NoTypos | MatcherScore.NoCaseTypos
                  | MatcherScore.ExactMiddleMatch | MatcherScore.WholeWordsMatch))
        == (MatcherScore.NoTypos | MatcherScore.NoCaseTypos
            | MatcherScore.ExactMiddleMatch | MatcherScore.WholeWordsMatch);

    public static bool IsExactPrefixMatch(this MatcherScore score) =>
        (score & (MatcherScore.NoTypos | MatcherScore.NoCaseTypos | MatcherScore.ExactPrefixMatch))
        == (MatcherScore.NoTypos | MatcherScore.NoCaseTypos | MatcherScore.ExactPrefixMatch);
}

namespace RoslynMCP.Lsp.Completion;

public enum IdentifierMatchingStyle
{
    /// <summary>The pattern must start at the identifier's first character.</summary>
    BeginningOfIdentifier,

    /// <summary>The pattern may start at any hump ("Builder" matches "StringBuilder").</summary>
    MiddleOfIdentifier,
}

/// <summary>Result of a successful match. <see cref="Score"/> is comparable as an integer.</summary>
public readonly record struct MatchResult(MatcherScore Score);

/// <summary>
/// CamelHumps matcher: every pattern character must land either directly after the previous
/// match or on a hump (word start) of the candidate, which is what makes "sb" hit
/// "StringBuilder" while rejecting the accidental substring hits a plain "contains" would
/// accept. When nothing aligns, a single typo (substitution or transposition) is corrected for
/// patterns long enough that a typo is the likelier explanation than a different word.
/// </summary>
/// <remarks>
/// Instances are immutable and safe to share across threads; matching allocates only the
/// per-candidate work arrays.
/// </remarks>
public sealed class IdentifierMatcher
{
    /// <summary>Longer candidates skip the alignment search — DP cost is length × pattern.</summary>
    private const int MaxCandidateLength = 256;

    private const int Unvisited = int.MinValue;
    private const int Impossible = int.MinValue + 1;

    private const int HumpBonus = 8;
    private const int ContiguousBonus = 4;
    private const int CaseBonus = 2;

    private readonly string _pattern;
    private readonly string _lowerPattern;
    private readonly IdentifierMatchingStyle _style;
    private readonly bool _correctTypos;

    public IdentifierMatcher(
        string pattern,
        IdentifierMatchingStyle style = IdentifierMatchingStyle.MiddleOfIdentifier,
        bool correctTypos = true)
    {
        _pattern = pattern;
        _lowerPattern = pattern.ToLowerInvariant();
        _style = style;
        _correctTypos = correctTypos;
    }

    /// <summary>Returns null when the candidate does not match at all.</summary>
    public MatchResult? Match(string candidate)
    {
        if (_pattern.Length == 0)
            return new MatchResult(MatcherScore.NoTypos | MatcherScore.NoCaseTypos | MatcherScore.CorrectOrder);
        if (candidate.Length == 0 || candidate.Length > MaxCandidateLength)
            return null;

        int[] humps = FindHumps(candidate);
        int[] positions = new int[_pattern.Length];
        if (TryAlign(candidate, humps, positions))
            return new MatchResult(Score(candidate, humps, positions));

        if (TryCorrectTypo(candidate))
        {
            for (int i = 0; i < positions.Length; i++)
                positions[i] = i;

            // A corrected match keeps its structural flags but never the clean-match ones —
            // that is what lets the ranker drop every typo hit as soon as one clean hit exists.
            var score = Score(candidate, humps, positions)
                        & ~(MatcherScore.NoTypos | MatcherScore.NoCaseTypos);
            return new MatchResult(score);
        }

        return null;
    }

    /// <summary>
    /// Word starts: index 0, an upper-case letter opening a word (including the last capital of
    /// a run — "HTMLParser" humps at H and P), a digit run, and anything after a separator.
    /// </summary>
    private static int[] FindHumps(string text)
    {
        Span<int> found = stackalloc int[text.Length];
        int count = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bool isHump;
            if (i == 0)
            {
                isHump = true;
            }
            else
            {
                char previous = text[i - 1];
                if (!char.IsLetterOrDigit(previous) && char.IsLetterOrDigit(c))
                    isHump = true;
                else if (char.IsUpper(c))
                    isHump = !char.IsUpper(previous) || (i + 1 < text.Length && char.IsLower(text[i + 1]));
                else if (char.IsDigit(c))
                    isHump = !char.IsDigit(previous);
                else
                    isHump = false;
            }

            if (isHump)
                found[count++] = i;
        }

        return found[..count].ToArray();
    }

    /// <summary>
    /// Picks the best alignment of the pattern onto the candidate, preferring hump starts,
    /// contiguity and matching case in that order. Returns false when no alignment exists.
    /// </summary>
    private bool TryAlign(string candidate, int[] humps, int[] positions)
    {
        int m = _pattern.Length;
        int n = candidate.Length;
        if (m > n)
            return false;

        int[] value = new int[m * n];
        int[] next = new int[m * n];
        value.AsSpan().Fill(Unvisited);

        int bestValue = Impossible;
        int bestStart = -1;

        // The pattern may only start where a word starts — at the identifier's first character,
        // or at any hump when middle matching is allowed.
        foreach (int start in _style == IdentifierMatchingStyle.BeginningOfIdentifier ? [0] : humps)
        {
            if (!Matches(0, candidate, start))
                continue;

            int rest = Solve(0, start);
            if (rest == Impossible)
                continue;

            int total = rest + Bonus(0, candidate, start, prev: -1, humps);
            if (total > bestValue)
            {
                bestValue = total;
                bestStart = start;
            }
        }

        if (bestStart < 0 || bestValue == Impossible)
            return false;

        int position = bestStart;
        for (int pi = 0; pi < m; pi++)
        {
            positions[pi] = position;
            if (pi + 1 < m)
                position = next[pi * n + position];
        }

        return true;

        // Best total bonus for pattern[pi..] given pattern[pi] sits at pos.
        int Solve(int pi, int pos)
        {
            if (pi == m - 1)
                return 0;

            int slot = pi * n + pos;
            if (value[slot] != Unvisited)
                return value[slot];

            value[slot] = Impossible;
            int best = Impossible;
            int bestNext = -1;

            // Contiguous continuation first, then every later hump.
            if (pos + 1 < n && Matches(pi + 1, candidate, pos + 1))
                Consider(pos + 1);

            foreach (int hump in humps)
            {
                if (hump <= pos + 1)
                    continue;
                if (Matches(pi + 1, candidate, hump))
                    Consider(hump);
            }

            value[slot] = best;
            next[slot] = bestNext;
            return best;

            void Consider(int candidatePos)
            {
                int rest = Solve(pi + 1, candidatePos);
                if (rest == Impossible)
                    return;

                int total = rest + Bonus(pi + 1, candidate, candidatePos, pos, humps);
                if (total > best)
                {
                    best = total;
                    bestNext = candidatePos;
                }
            }
        }
    }

    private int Bonus(int patternIndex, string text, int pos, int prev, int[] humps)
    {
        int bonus = 0;
        if (humps.BinarySearch(pos) >= 0)
            bonus += HumpBonus;
        if (prev >= 0 && pos == prev + 1)
            bonus += ContiguousBonus;
        if (text[pos] == _pattern[patternIndex])
            bonus += CaseBonus;
        return bonus;
    }

    private bool Matches(int patternIndex, string text, int pos) =>
        char.ToLowerInvariant(text[pos]) == _lowerPattern[patternIndex];

    /// <summary>
    /// One substitution or one transposition against the candidate's prefix. Gated on length
    /// because at three characters a "typo" is usually just a different identifier.
    /// </summary>
    private bool TryCorrectTypo(string candidate)
    {
        if (!_correctTypos)
            return false;

        int m = _pattern.Length;
        if (m < 5 || candidate.Length < m)
            return false;
        if (char.ToLowerInvariant(candidate[0]) != _lowerPattern[0])
            return false;

        int i = 1;
        while (i < m && char.ToLowerInvariant(candidate[i]) == _lowerPattern[i])
            i++;

        if (i == m)
            return true;
        if (i == m - 1)
            return true; // wrong (or extra) last character

        char patternNext = _lowerPattern[i + 1];
        char candidateHere = char.ToLowerInvariant(candidate[i]);
        char candidateNext = char.ToLowerInvariant(candidate[i + 1]);

        bool substitution = candidateNext == patternNext;
        bool transposition = candidateHere == patternNext && candidateNext == _lowerPattern[i];
        if (!substitution && !transposition)
            return false;

        i += 2;
        while (i < m && char.ToLowerInvariant(candidate[i]) == _lowerPattern[i])
            i++;
        return i == m;
    }

    private MatcherScore Score(string candidate, int[] humps, int[] positions)
    {
        int m = positions.Length;
        var score = MatcherScore.NoTypos | MatcherScore.NoCaseTypos | MatcherScore.CorrectOrder;

        for (int i = 0; i < m; i++)
        {
            if (candidate[positions[i]] != _pattern[i])
            {
                score &= ~MatcherScore.NoCaseTypos;
                break;
            }
        }

        if (positions[m - 1] == m - 1 && positions[0] == 0)
        {
            score |= MatcherScore.ExactPrefixMatch;
            if (m == candidate.Length)
                score |= MatcherScore.ExactMatch;
        }

        if (positions[0] == humps[0])
            score |= MatcherScore.FirstHumpMatch;

        if (positions[0] + m - 1 == positions[m - 1])
            score |= MatcherScore.ExactMiddleMatch;

        if (AllHumpsMatched(humps, positions, out bool noGaps))
            score |= MatcherScore.AllHumpsMatch;
        if (noGaps)
            score |= MatcherScore.NoGapsBetweenHumps;

        if (WholeWordsMatched(candidate, humps, positions))
            score |= MatcherScore.WholeWordsMatch;

        int firstLetter = 0;
        while (firstLetter < m && !char.IsLetter(_pattern[firstLetter]))
            firstLetter++;
        if (firstLetter < m
            && humps.BinarySearch(positions[firstLetter]) >= 0
            && candidate[positions[firstLetter]] == _pattern[firstLetter])
            score |= MatcherScore.FirstLetterHumpCaseMatch;

        return score;
    }

    /// <summary>True when every hump of the candidate got a pattern character.</summary>
    private static bool AllHumpsMatched(int[] humps, int[] positions, out bool noGaps)
    {
        noGaps = true;
        bool all = true;
        int previousMatchedHump = -1;

        for (int h = 0; h < humps.Length; h++)
        {
            bool matched = Array.BinarySearch(positions, humps[h]) >= 0;
            if (!matched)
            {
                all = false;
                continue;
            }

            if (previousMatchedHump >= 0 && h - previousMatchedHump > 1)
                noGaps = false;
            previousMatchedHump = h;
        }

        return all;
    }

    /// <summary>
    /// True when each contiguous run of matched characters covers its word to the end (or the
    /// pattern simply ran out) — "strBui" against "StringBuilder" is not whole-words, "strBuilder" is.
    /// </summary>
    private static bool WholeWordsMatched(string candidate, int[] humps, int[] positions)
    {
        int i = 0;
        while (i < positions.Length)
        {
            int start = positions[i];
            int end = start;
            while (i + 1 < positions.Length && positions[i + 1] == end + 1)
            {
                end = positions[++i];
            }

            i++;
            if (i == positions.Length)
                return true; // trailing run may stop anywhere

            if (humps.BinarySearch(start) < 0)
                return false;

            int wordEnd = candidate.Length - 1;
            foreach (int hump in humps)
            {
                if (hump > start)
                {
                    wordEnd = hump - 1;
                    break;
                }
            }

            if (end != wordEnd)
                return false;
        }

        return true;
    }
}

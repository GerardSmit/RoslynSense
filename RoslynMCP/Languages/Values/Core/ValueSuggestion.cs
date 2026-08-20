namespace RoslynMCP.Languages.Values.Core;

/// <summary>
/// The value someone probably meant.
/// </summary>
/// <remarks>
/// Worth the arithmetic because of what these strings are. Codes are long, lowercase and full of
/// underscores — <c>order_wait_for_login</c> — which is the exact shape a person mistypes and the
/// exact shape nobody spots by eye afterwards. "That is not a valid code" leaves the reader to
/// diff two strings by hand; "did you mean <c>order_wait_for_login</c>" ends it.
/// </remarks>
internal static class ValueSuggestion
{
    /// <summary>
    /// The closest value to what was written, or null when nothing is close enough to be a guess
    /// rather than a distraction.
    /// </summary>
    public static string? Nearest(ValueSetContents contents, string written, StringComparer comparer)
    {
        if (written.Length == 0)
            return null;

        // A case-only miss is not a typo, it is a spelling, and it is what a case-sensitive set is
        // for. Answered first so it never loses to some other value one edit away.
        foreach (var entry in contents.Values)
        {
            if (!comparer.Equals(entry.Value, written)
                && string.Equals(entry.Value, written, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Value;
            }
        }

        // A third of the string, so a short code tolerates one slip and a long one several, and
        // nothing unrelated ever qualifies.
        int budget = Math.Max(1, written.Length / 3);
        string? best = null;

        foreach (var entry in contents.Values)
        {
            int distance = Distance(entry.Value, written, budget);

            if (distance <= budget)
            {
                budget = distance;
                best = entry.Value;

                if (distance == 0)
                    break;
            }
        }

        return best;
    }

    /// <summary>
    /// Levenshtein distance, abandoned as soon as it cannot come in under <paramref name="budget"/>.
    /// </summary>
    /// <remarks>
    /// Two rows rather than a matrix, and the length check in front: this runs once per value per
    /// reported literal, and a set can be two thousand of them.
    /// </remarks>
    private static int Distance(string candidate, string typed, int budget)
    {
        int over = budget + 1;

        if (Math.Abs(candidate.Length - typed.Length) > budget)
            return over;

        int[] previous = new int[typed.Length + 1];
        int[] current = new int[typed.Length + 1];

        for (int i = 0; i <= typed.Length; i++)
            previous[i] = i;

        for (int i = 1; i <= candidate.Length; i++)
        {
            current[0] = i;
            int row = current[0];

            for (int j = 1; j <= typed.Length; j++)
            {
                int cost = char.ToLowerInvariant(candidate[i - 1]) == char.ToLowerInvariant(typed[j - 1])
                    ? 0
                    : 1;

                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);

                row = Math.Min(row, current[j]);
            }

            // Every distance from here on is at least the best in this row, so a row entirely over
            // the budget is the end of it.
            if (row > budget)
                return over;

            (previous, current) = (current, previous);
        }

        return previous[typed.Length];
    }
}

using System.Collections.Immutable;

namespace RoslynMCP.Languages.WebForms.Core;

/// <summary>What a pasted <c>ClientID</c> or <c>UniqueID</c> decomposes into.</summary>
/// <param name="Kept">The hand-written segments, in order, with the generated ones removed.</param>
/// <param name="Exact">
/// Whether the segmentation is certain. A <c>UniqueID</c> separates with <c>$</c>, which no
/// <c>ID</c> may contain, so its split is exact; a <c>ClientID</c> separates with <c>_</c>, which
/// an <c>ID</c> may itself contain, so <c>OrderPortal_Intake_View</c> is one segment or three and
/// only the markup can say which.
/// </param>
internal sealed record ClientIdSegments(ImmutableArray<string> Kept, bool Exact)
{
    /// <summary>
    /// The same id read with the runtime's row numbers taken out, or null when it has none.
    /// </summary>
    /// <remarks>
    /// <c>ClientIDMode="Predictable"</c> — what a data-bound control uses by default — numbers the
    /// rows of a repeated template by appending the index to the id it just generated, so the save
    /// button in the third row of <c>rptBackorder</c> is <c>…_rptBackorder_btnSave_2</c> and no
    /// markup anywhere declares a <c>btnSave_2</c>. Nested templates leave one in the middle as
    /// well, since the inner repeater's own id was numbered before the button's was:
    /// <c>…_rptOuter_rptInner_0_btnSave_1</c>.
    /// <para>
    /// A second reading rather than a correction, because an <c>ID</c> may legally contain
    /// <c>_2</c> — <c>Step_1_Detail</c> is somebody's control — and nothing in the id says which of
    /// the two this is. The markup decides, the same way it decides where an underscored id begins.
    /// </para>
    /// </remarks>
    public ClientIdSegments? WithoutRowNumbers()
    {
        if (!Kept.Any(RowNumber))
            return null;

        var kept = Kept.Where(segment => !RowNumber(segment)).ToImmutableArray();

        return kept.IsEmpty ? null : new ClientIdSegments(kept, Exact);
    }

    /// <summary>
    /// A segment that is nothing but digits, which no hand-written <c>ID</c> can be.
    /// </summary>
    /// <remarks>
    /// An <c>ID</c> has to begin with a letter or an underscore, so digits alone were written by
    /// the runtime — even where the underscore in front of them was not.
    /// </remarks>
    private static bool RowNumber(string segment) => segment.All(char.IsAsciiDigit);
}

/// <summary>
/// Whether a query is a runtime control id, and what it is made of.
/// </summary>
/// <remarks>
/// Pure, and that is the point: this runs on the query alone, before a solution is looked up or a
/// markup file is opened, so an ordinary search never pays for the markup path. The gate is
/// deliberately narrow — a query has to carry a segment ASP.NET or DNN generated, not merely look
/// underscored — because the alternative is that <c>MAX_BUFFER_SIZE</c> and every
/// <c>snake_case_name</c> in the solution start walking control trees.
/// </remarks>
internal static class ClientIdQuery
{
    /// <summary>Shorter than this and the ordinary search is the better answer anyway.</summary>
    private const int MinLength = 8;

    /// <summary>
    /// Whether the query is shaped like an id the runtime composed.
    /// </summary>
    /// <remarks>
    /// Three things have to hold. No character the ordinary query syntax already gives meaning to,
    /// since <c>Customer.cs:851</c> and <c>Shop.Add</c> are asking something else entirely. A
    /// separator: either a <c>$</c>, or at least three <c>_</c>-separated segments. And at least
    /// one segment the user cannot have typed on purpose — <c>ctl12</c>, <c>ctr1848</c>, or a
    /// leading <c>dnn</c>. That last clause is the whole gate: without it every
    /// <c>Order_Item_Total</c> in the solution takes this path.
    /// </remarks>
    public static bool LooksLikeClientId(ReadOnlySpan<char> query)
    {
        if (query.Length < MinLength)
            return false;

        char separator = query.Contains('$') ? '$' : '_';
        int segments = 0;
        bool generated = false;

        for (int i = 0, start = 0; i <= query.Length; i++)
        {
            if (i < query.Length)
            {
                if (Reserved(query[i]))
                    return false;

                if (query[i] != separator)
                    continue;
            }

            var segment = query[start..i];
            start = i + 1;

            if (segment.IsEmpty)
                continue;

            segments++;
            generated |= Generated(segment) || (segments == 1 && Dnn(segment));
        }

        return generated && (separator == '$' || segments >= 3);
    }

    /// <summary>The hand-written segments of the query, or null when it is not a control id.</summary>
    public static ClientIdSegments? Parse(string query)
    {
        if (!LooksLikeClientId(query))
            return null;

        bool exact = query.Contains('$');
        var parts = query.Split(exact ? '$' : '_', StringSplitOptions.RemoveEmptyEntries);
        var kept = ImmutableArray.CreateBuilder<string>(parts.Length);

        for (int i = 0; i < parts.Length; i++)
        {
            // Only a leading `dnn` is dropped. A control someone deliberately called `dnn` further
            // in is theirs, and dropping it would resolve their id to the wrong control.
            if (Generated(parts[i]) || (i == 0 && Dnn(parts[i])))
                continue;

            kept.Add(parts[i]);
        }

        return kept.Count == 0 ? null : new ClientIdSegments(kept.ToImmutable(), exact);
    }

    /// <summary>
    /// <c>ctl00</c> and <c>ctr1848</c>: a segment ASP.NET or DNN numbered rather than named.
    /// </summary>
    /// <remarks>
    /// The digits are required. <c>ctlSaveButton</c> is somebody's hand-written id and dropping it
    /// would lose the only segment that says which control the paste is about.
    /// </remarks>
    private static bool Generated(ReadOnlySpan<char> segment)
    {
        if (segment.Length < 4
            || (!segment.StartsWith("ctl", StringComparison.Ordinal)
                && !segment.StartsWith("ctr", StringComparison.Ordinal)))
        {
            return false;
        }

        foreach (char c in segment[3..])
        {
            if (!char.IsAsciiDigit(c))
                return false;
        }

        return true;
    }

    private static bool Dnn(ReadOnlySpan<char> segment) =>
        segment.Equals("dnn", StringComparison.OrdinalIgnoreCase);

    /// <summary>The characters the ordinary query syntax already reads as something else.</summary>
    private static bool Reserved(char c) =>
        c is ' ' or '.' or '/' or '\\' or ':' or '+';
}

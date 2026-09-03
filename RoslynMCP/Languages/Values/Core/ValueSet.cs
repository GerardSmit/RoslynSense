using System.Collections.Immutable;

namespace RoslynMCP.Languages.Values.Core;

/// <summary>
/// One named set of allowed string values, as <c>roslynsense.json</c> declares it: either a query
/// against a registered connection, or a list written out.
/// </summary>
/// <remarks>
/// The definition and the values behind it are deliberately separate types. A definition is cheap,
/// always available and safe to compare against; the values are a database round trip that can
/// fail, be slow, or be out of date. Everything that decides <i>whether a literal is one of these</i>
/// needs both, and everything that decides <i>whether this literal is any of our business</i> needs
/// only the first — which is why detection never touches a connection.
/// </remarks>
internal sealed record ValueSetDefinition
{
    /// <summary>What a binding names this set by.</summary>
    public required string Id { get; init; }

    /// <summary>The connection alias, or null to use the only registered connection.</summary>
    public string? Connection { get; init; }

    /// <summary>The query behind the set, or null for a set written out in the file.</summary>
    public string? Query { get; init; }

    /// <summary>The values as the file gave them. Used when <see cref="Query"/> is absent.</summary>
    public ImmutableArray<ValueEntry> Inline { get; init; } = [];

    /// <summary>Whether a literal has to match a value's casing exactly.</summary>
    public bool CaseSensitive { get; init; }

    public bool FromDatabase => !string.IsNullOrWhiteSpace(Query);

    /// <summary>How a literal is compared against the set's values.</summary>
    /// <remarks>
    /// Case-insensitive by default because the comparison the code does usually is —
    /// <c>string.Equals(code, "x", StringComparison.OrdinalIgnoreCase)</c>, a case-insensitive
    /// database collation, or both. A set whose values differ only by case is the one that needs
    /// the strict comparer, and that is rare enough to be the opt-in.
    /// </remarks>
    public StringComparer Comparer =>
        CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Where the values come from, in one phrase, for a surface that is about the set itself.
    /// </summary>
    /// <remarks>
    /// Kept off the diagnostic and off hover. Both of those are read while looking at one status
    /// code, and a query — long, joined, ordered — answers a question nobody was asking there while
    /// crowding out the ones they were. Where the values come from is a fact about the
    /// configuration, and it belongs wherever the configuration is what is on screen.
    /// </remarks>
    public string Origin =>
        FromDatabase
            ? Connection is { Length: > 0 } alias ? $"`{alias}`: `{Query}`" : $"`{Query}`"
            : "the values listed in `roslynsense.json`";
}

/// <summary>One allowed value, and whatever the second column of the query called it.</summary>
/// <param name="Value">The string the C# has to be.</param>
/// <param name="Label">A human name for it, or null. Shown beside the value, never compared.</param>
internal readonly record struct ValueEntry(string Value, string? Label);

/// <summary>Whether a set's values are in hand.</summary>
internal enum ValueSetState
{
    /// <summary>The values loaded.</summary>
    Ready,

    /// <summary>They did not, and <see cref="ValueSetContents.Problem"/> says why.</summary>
    Unavailable,
}

/// <summary>
/// The values of one set as they currently stand, and whether they can be trusted to be all of them.
/// </summary>
/// <remarks>
/// <see cref="Decides"/> is the whole point of this type. Reporting "that is not a valid code" is a
/// claim about every code there is, so it needs the full set: a database that is unreachable, a
/// query that failed, and a result the row cap cut short all have values worth <i>offering</i> and
/// none worth <i>judging by</i>. Conflating the two is how a feature meant to catch typos ends up
/// putting a red squiggle under correct code whenever the network hiccups.
/// </remarks>
internal sealed class ValueSetContents
{
    private readonly HashSet<string> _index;
    private readonly StringComparer _comparer;

    private ValueSetContents(
        ValueSetState state, ImmutableArray<ValueEntry> values, bool complete, string? problem,
        StringComparer comparer)
    {
        State = state;
        Values = values;
        Complete = complete;
        Problem = problem;
        _comparer = comparer;
        _index = new HashSet<string>(values.Select(entry => entry.Value), comparer);
    }

    public ValueSetState State { get; }

    /// <summary>The values, in the order the query returned them.</summary>
    public ImmutableArray<ValueEntry> Values { get; }

    /// <summary>Whether <see cref="Values"/> is every value there is.</summary>
    public bool Complete { get; }

    /// <summary>Why the values are missing or partial, in one sentence.</summary>
    public string? Problem { get; }

    /// <summary>Whether this is a sound basis for saying a literal is wrong.</summary>
    public bool Decides => State == ValueSetState.Ready && Complete;

    public bool Contains(string value) => _index.Contains(value);

    /// <summary>The entry for a value, so hover can show the label beside it.</summary>
    public ValueEntry? Find(string value)
    {
        foreach (var entry in Values)
        {
            if (_comparer.Equals(entry.Value, value))
                return entry;
        }

        return null;
    }

    public static ValueSetContents Loaded(
        ValueSetDefinition set, ImmutableArray<ValueEntry> values, bool complete,
        string? problem = null) =>
        new(ValueSetState.Ready, values, complete, problem, set.Comparer);

    public static ValueSetContents Unavailable(ValueSetDefinition set, string problem) =>
        new(ValueSetState.Unavailable, [], false, problem, set.Comparer);
}

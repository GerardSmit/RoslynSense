using System.Diagnostics;
using RoslynMCP.Services;

namespace RoslynMCP.Lsp.Search;

/// <summary>
/// Where a slow search spent its time, split between waiting for the solution and searching it.
/// </summary>
/// <remarks>
/// <para>
/// The two halves are worth telling apart because only one of them is a search problem. A first
/// Ctrl+T on a cold daemon used to cost about ten seconds, and nearly all of it was
/// <see cref="SolutionWarmup"/> evaluating every project — the matching underneath is milliseconds
/// once the indexes exist. Without the split, that reads as "search is slow" and sends anyone
/// looking at it into the ranking code, which is the one part that was never the problem.
/// </para>
/// <para>
/// Only searches past <see cref="Interesting"/> are logged, and at info level, so this lands in the
/// output channel rather than in front of the user. A search box is asked a question per keystroke:
/// a line per query would be a log nobody reads, and the fast ones have nothing to say anyway.
/// </para>
/// </remarks>
internal struct SearchTimer
{
    /// <summary>Below this a search is not worth a line. Well under the ~750ms where a wait stops
    /// reading as "instant", so anything a user would call slow is recorded.</summary>
    private static readonly TimeSpan Interesting = TimeSpan.FromMilliseconds(400);

    private readonly string _what;
    private readonly string _query;
    private readonly string? _filter;
    private readonly long _started;
    private long _searchStarted;

    private SearchTimer(string what, string query, string? filter)
    {
        _what = what;
        _query = query;
        _filter = filter;
        _started = Stopwatch.GetTimestamp();
        _searchStarted = _started;
    }

    /// <param name="filter">What narrowed the search — the panel's tab, say — worded for the log
    /// line. A query that finds 0 results under one tab and 51 under another looks like a bug
    /// until the line says which tab each answer was for; it cost an investigation to learn that.</param>
    public static SearchTimer Start(string what, string query, string? filter = null) =>
        new(what, query, filter);

    /// <summary>The moment the corpus was ready and the search itself began.</summary>
    public void CorpusReady() => _searchStarted = Stopwatch.GetTimestamp();

    /// <param name="corpus">Which corpus answered — the loaded solution, or the load-free name
    /// index that stands in for it while the solution is still being evaluated.</param>
    public readonly void Done(int results, string corpus)
    {
        var total = Stopwatch.GetElapsedTime(_started);
        if (total < Interesting)
            return;

        var searching = Stopwatch.GetElapsedTime(_searchStarted);
        var waiting = total - searching;

        ServiceLog.Info(
            $"{_what} \"{_query}\"{(_filter is null ? "" : $" ({_filter})")} took {Seconds(total)} " +
            $"against the {corpus}: " +
            $"{Seconds(waiting)} waiting for the corpus, {Seconds(searching)} searching it " +
            $"({results} result{(results == 1 ? "" : "s")}).");
    }

    private static string Seconds(TimeSpan span) => $"{span.TotalSeconds:0.00}s";
}

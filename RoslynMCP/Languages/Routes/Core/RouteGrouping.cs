using System.Collections.Immutable;

namespace RoslynMCP.Languages.Routes.Core;

/// <summary>One level of the path tree: the branches under a prefix, and what sits directly on it.</summary>
/// <param name="Groups">Branches worth a row of their own, in path order.</param>
/// <param name="Leaves">Endpoints with nowhere further to go, in path then verb order.</param>
internal readonly record struct RouteLevel(
    ImmutableArray<RouteBranch> Groups,
    ImmutableArray<RouteEndpoint> Leaves);

/// <summary>A path prefix that more than one endpoint shares.</summary>
/// <param name="Prefix">The whole prefix, from the root — what a child level is asked for.</param>
/// <param name="Label">Only the part this row adds, which is what the row shows.</param>
/// <param name="Count">How many endpoints sit under it, at any depth.</param>
internal readonly record struct RouteBranch(string Prefix, string Label, int Count);

/// <summary>
/// The endpoints of a project, as a tree of shared path prefixes.
/// </summary>
/// <remarks>
/// <para>
/// A flat list of two hundred paths is a list to read rather than a thing to navigate, and it
/// repeats the same <c>/api/v1</c> two hundred times down the left-hand column. Grouping by what
/// the paths share turns the left-hand column into the shape of the API — which is the question
/// somebody opening this section is asking.
/// </para>
/// <para>
/// A branch is drawn only where more than one endpoint shares the prefix, so a lone
/// <c>/health</c> stays where it is instead of becoming a folder holding one thing. Chains with
/// nothing to choose between them are collapsed into one row, so a solution whose paths all begin
/// <c>/api/v1</c> gets one <c>/api/v1</c> row rather than an <c>/api</c> holding a <c>/v1</c> —
/// the difference between a tree that says something and a tree that has to be clicked through.
/// </para>
/// <para>
/// Pure, and separated from the rows for that reason: the shape is the part with rules worth
/// pinning, and none of them need a workspace.
/// </para>
/// </remarks>
internal static class RouteGrouping
{
    /// <summary>The branches and leaves directly under <paramref name="prefix"/>.</summary>
    /// <param name="endpoints">Every endpoint of the project; filtered here.</param>
    /// <param name="prefix">Empty for the project's own row.</param>
    public static RouteLevel Level(IEnumerable<RouteEndpoint> endpoints, string prefix)
    {
        var consumed = Segments(prefix);

        var groups = ImmutableArray.CreateBuilder<RouteBranch>();
        var leaves = ImmutableArray.CreateBuilder<RouteEndpoint>();

        // A path nobody could read has no prefix to sit under, so it belongs to the project rather
        // than to any branch — and putting it under a guessed one would be the guess this whole
        // pack refuses to make.
        var buckets = new Dictionary<string, List<RouteEndpoint>>(StringComparer.OrdinalIgnoreCase);

        foreach (var endpoint in endpoints)
        {
            if (endpoint.Path.Text is not { Length: > 0 } path)
            {
                if (consumed.Length == 0)
                    leaves.Add(endpoint);

                continue;
            }

            var segments = Segments(path);
            if (!StartsWith(segments, consumed))
                continue;

            // The endpoint is at the prefix itself — `/api/v1` served, with `/api/v1/users`
            // beside it. It has no next segment to be bucketed by.
            if (segments.Length == consumed.Length)
            {
                leaves.Add(endpoint);
                continue;
            }

            string next = segments[consumed.Length];

            if (!buckets.TryGetValue(next, out var bucket))
                buckets[next] = bucket = [];

            bucket.Add(endpoint);
        }

        foreach (var (_, bucket) in buckets.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (bucket.Count == 1)
            {
                leaves.Add(bucket[0]);
                continue;
            }

            var shared = Shared(bucket, consumed.Length);

            groups.Add(new RouteBranch(
                Prefix: "/" + string.Join('/', consumed.Concat(shared)),
                Label: "/" + string.Join('/', shared),
                Count: bucket.Count));
        }

        return new RouteLevel(
            [.. groups],
            [.. leaves
                .OrderBy(endpoint => endpoint.Path.Text ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(endpoint => endpoint.Verb ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(endpoint => endpoint.Offset)]);
    }

    /// <summary>What an endpoint's row shows once the branches above it have said their part.</summary>
    /// <remarks>
    /// The remainder rather than the whole path: a row reading <c>GET /api/v1/orders</c> under a
    /// branch already reading <c>/api/v1</c> says the prefix twice and pushes the part that differs
    /// off the right of a narrow panel. An endpoint sitting on the branch itself has no remainder
    /// and shows <c>/</c>, which is what it serves.
    /// </remarks>
    public static string Remainder(RouteEndpoint endpoint, string prefix)
    {
        if (endpoint.Path.Text is not { Length: > 0 } path)
            return string.Empty;

        var consumed = Segments(prefix);
        if (consumed.Length == 0)
            return path;

        var segments = Segments(path);
        if (!StartsWith(segments, consumed))
            return path;

        return segments.Length == consumed.Length
            ? "/"
            : "/" + string.Join('/', segments[consumed.Length..]);
    }

    /// <summary>How many segments deep two or more paths agree, past what is already consumed.</summary>
    private static string[] Shared(List<RouteEndpoint> bucket, int from)
    {
        var first = Segments(bucket[0].Path.Text!);
        int length = first.Length - from;

        foreach (var endpoint in bucket.Skip(1))
        {
            var segments = Segments(endpoint.Path.Text!);
            int agree = 0;

            while (agree < length
                && from + agree < segments.Length
                && segments[from + agree].Equals(first[from + agree], StringComparison.OrdinalIgnoreCase))
            {
                agree++;
            }

            length = agree;
        }

        // At least the segment they were bucketed by. An endpoint served at exactly the branch
        // prefix would otherwise shrink the branch to nothing and lose its own bucket.
        return first[from..(from + Math.Max(length, 1))];
    }

    private static string[] Segments(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static bool StartsWith(string[] segments, string[] prefix)
    {
        if (segments.Length < prefix.Length)
            return false;

        for (int i = 0; i < prefix.Length; i++)
        {
            if (!segments[i].Equals(prefix[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
}

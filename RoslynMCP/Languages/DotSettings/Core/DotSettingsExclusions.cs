using System.Collections.Concurrent;
using System.Collections.Immutable;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.DotSettings.Core;

/// <summary>
/// "Did the team exclude this file from analysis", asked from the places that have a path and
/// nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The exclusion is stored per project but asked per file, by a search that is walking a directory
/// tree and does not know which project — if any — a candidate belongs to. So the sets are unioned
/// across the solution once and asked as one. A file excluded by the project that owns it is
/// excluded, and no other project's list can be reached by a path outside that project anyway,
/// because every spec resolves to an absolute path under its own project's directory.
/// </para>
/// <para>
/// Static, and gated by <see cref="Enabled"/>, because the callers are static helpers on a search
/// path where threading a service through would mean rewriting the search to carry a container it
/// otherwise has no use for. The gate is set once when the pack is registered; with the pack off,
/// every call here returns false and the behaviour is exactly what it was before this existed.
/// </para>
/// </remarks>
internal static class DotSettingsExclusions
{
    /// <summary>
    /// Whether the <c>.DotSettings</c> pack is registered for this process. Off until a host says
    /// otherwise, so a host that never calls <see cref="LanguagePackRegistration"/> — the tests
    /// that construct services by hand — behaves as if the feature did not exist.
    /// </summary>
    public static bool Enabled { get; set; }

    /// <summary>
    /// How long a unioned index is trusted before its layers are stat-ed again. A search asks this
    /// once per candidate file, thousands of times in a burst, and re-stat-ing every layer for
    /// each of them would cost more than the search; a couple of seconds is under the time it
    /// takes to notice a settings edit and re-run a search.
    /// </summary>
    private static readonly TimeSpan RecheckAfter = TimeSpan.FromSeconds(2);

    private sealed record Index(
        ImmutableHashSet<string> Paths,
        ImmutableArray<System.Text.RegularExpressions.Regex> Masks,
        DateTime CheckedUtc)
    {
        public bool IsEmpty => Paths.IsEmpty && Masks.IsEmpty;
    }

    private static readonly ConcurrentDictionary<string, Index> s_cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a <c>.DotSettings</c> layer in this file's solution excludes it.</summary>
    public static bool IsExcluded(string absolutePath)
    {
        try
        {
            return IsExcludedCore(absolutePath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The answer is advisory and the callers are searches asking about thousands of
            // paths per keystroke — some of which a loaded project still lists after their
            // folder was deleted on disk. One unanswerable path must degrade to "not excluded",
            // never take the whole search with it.
            return false;
        }
    }

    private static bool IsExcludedCore(string absolutePath)
    {
        if (!Enabled || string.IsNullOrEmpty(absolutePath))
            return false;

        if (PathHelper.FindNearestSolution(absolutePath) is not { Length: > 0 } solution)
            return false;

        var index = IndexFor(solution);

        if (index.IsEmpty)
            return false;

        string normalized;

        try
        {
            normalized = PathHelper.NormalizePath(absolutePath);
        }
        catch (ArgumentException)
        {
            return false;
        }

        string? current = normalized;

        while (current is { Length: > 0 })
        {
            if (index.Paths.Contains(current))
                return true;

            current = Path.GetDirectoryName(current);
        }

        if (index.Masks.IsEmpty)
            return false;

        string name = Path.GetFileName(normalized);

        foreach (var mask in index.Masks)
        {
            if (mask.IsMatch(name))
                return true;
        }

        return false;
    }

    /// <summary>Drops the unioned indexes. For tests, and for a solution close.</summary>
    public static void Clear()
    {
        s_cache.Clear();
        ReSharperSettings.Clear();
    }

    private static Index IndexFor(string solutionPath)
    {
        var now = DateTime.UtcNow;

        if (s_cache.TryGetValue(solutionPath, out var cached)
            && now - cached.CheckedUtc < RecheckAfter)
        {
            return cached;
        }

        var paths = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        var masks = ImmutableArray.CreateBuilder<System.Text.RegularExpressions.Regex>();

        // The solution's own layers count too: a team may exclude from the .sln.DotSettings rather
        // than from each project. ForProject takes the nearest solution into account either way,
        // so passing the solution path itself is what covers the no-project case.
        foreach (string owner in Owners(solutionPath))
        {
            var settings = ReSharperSettings.ForProject(owner);

            if (settings.IsEmpty)
                continue;

            paths.UnionWith(settings.ExcludedPaths);
            masks.AddRange(settings.FileMasks);
        }

        var index = new Index(paths.ToImmutable(), masks.ToImmutable(), now);
        s_cache[solutionPath] = index;
        return index;
    }

    private static IEnumerable<string> Owners(string solutionPath)
    {
        yield return solutionPath;

        List<string> projects;

        try
        {
            projects = PathHelper.GetProjectsFromSolution(solutionPath);
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (string project in projects)
            yield return project;
    }
}

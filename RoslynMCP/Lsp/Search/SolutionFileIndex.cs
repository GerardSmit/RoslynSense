using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;

namespace RoslynMCP.Lsp.Search;

/// <summary>
/// Every file under the solution's project folders, not just the ones the compiler reads.
/// </summary>
/// <remarks>
/// Roslyn's document set answers "what does this compilation contain", which is the wrong
/// question for a file search: a <c>.proto</c>, a <c>.json</c> or a <c>.md</c> is invisible there,
/// so searching for one found nothing. The index walks each project's directory instead, which is
/// also where build output gets dropped on the floor (see <see cref="SearchFileRules"/>).
///
/// Cached per directory with a short TTL rather than watched: a stale entry costs a file that is
/// seconds old, and the walk is cheap enough to redo — while a watcher over every project folder
/// is a subsystem, and one more thing to get wrong on a rename.
/// </remarks>
public static class SolutionFileIndex
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentDictionary<string, Entry> s_cache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Entry(DateTime BuiltUtc, IReadOnlyList<string> Files);

    public static async Task<IReadOnlyList<string>> FilesAsync(Solution solution, CancellationToken ct)
    {
        var roots = Roots(solution);
        var results = await Task.WhenAll(roots.Select(root => Task.Run(() => Files(root, ct), ct)));

        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in results.SelectMany(r => r))
        {
            if (seen.Add(file))
                files.Add(file);
        }

        return files;
    }

    public static void Clear() => s_cache.Clear();

    /// <summary>
    /// The cached walk for one directory, for a caller that has a root rather than a
    /// <see cref="Solution"/> — the solution-tree search fields a request per keystroke, and it
    /// worked from the <c>.sln</c> on disk precisely so it never has to wait for Roslyn.
    /// </summary>
    public static IReadOnlyList<string> FilesUnder(string root, CancellationToken ct) =>
        Files(Path.GetFullPath(root), ct);

    /// <summary>
    /// One directory per project, minus any that sit inside another — walking a parent twice is
    /// the difference between one pass over a repo and one pass per project in it.
    /// </summary>
    private static IReadOnlyList<string> Roots(Solution solution)
    {
        // The solution's own folder first: a README, a .props or the .sln itself lives beside the
        // projects, not inside one, and a file search that skipped them would be a half-answer.
        var solutionDirectory = solution.FilePath is { Length: > 0 } solutionPath
            ? Path.GetDirectoryName(Path.GetFullPath(solutionPath))
            : null;

        var directories = solution.Projects
            .Select(project => project.FilePath)
            .OfType<string>()
            .Select(Path.GetDirectoryName)
            .OfType<string>()
            .Select(Path.GetFullPath)
            .Prepend(solutionDirectory)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(directory => directory.Length)
            .ToList();

        var roots = new List<string>();
        foreach (string directory in directories)
        {
            if (!roots.Any(root => IsUnder(directory, root)))
                roots.Add(directory);
        }

        return roots;
    }

    private static bool IsUnder(string path, string root) =>
        path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> Files(string root, CancellationToken ct)
    {
        if (s_cache.TryGetValue(root, out var cached) && DateTime.UtcNow - cached.BuiltUtc < Ttl)
            return cached.Files;

        var files = new List<string>();
        try
        {
            Walk(root, files, depth: 0, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException) { /* a directory vanished mid-walk — report what was found */ }
        catch (UnauthorizedAccessException) { }

        s_cache[root] = new Entry(DateTime.UtcNow, files);
        return files;
    }

    /// <summary>
    /// Hand-rolled rather than <c>EnumerateFiles(AllDirectories)</c> so that an excluded
    /// directory is never descended into: skipping <c>obj/</c> after enumerating it still pays
    /// for enumerating it, and on a big solution that is most of the walk.
    /// </summary>
    private static void Walk(string directory, List<string> files, int depth, CancellationToken ct)
    {
        const int MaxDepth = 32;
        if (depth > MaxDepth)
            return;

        ct.ThrowIfCancellationRequested();

        foreach (string file in Directory.EnumerateFiles(directory))
            files.Add(file);

        foreach (string child in Directory.EnumerateDirectories(directory))
        {
            if (SearchFileRules.IsExcluded(Path.GetFileName(child)))
                continue;

            try
            {
                Walk(child, files, depth + 1, ct);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

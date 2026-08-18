using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;

namespace RoslynMCP.Lsp.Search;

/// <summary>One line that contains the query. Positions are 0-based, in characters.</summary>
public sealed record TextHit(string FilePath, int Line, int Character, string LineText);

/// <summary>
/// The Text tab of Search Everywhere: a literal, case-insensitive scan over every file the
/// solution index knows about.
/// </summary>
/// <remarks>
/// A scan, not an index: the corpus is the same directory walk the file search uses, and at
/// solution scale a fresh scan answers well inside a keystroke's budget — a 14k-file
/// solution scans in well under a second. Binary files are skipped by extension and then by a
/// NUL probe, because "search everywhere" must not answer with a match inside a .dll.
/// </remarks>
public static class TextSearch
{
    /// <summary>Anything bigger is generated or data, and scanning it steals the budget.</summary>
    private const long MaxFileBytes = 2 * 1024 * 1024;

    /// <summary>How much of a matched line the result carries — enough to read, never a payload.</summary>
    private const int MaxLineLength = 240;

    public static async Task<(IReadOnlyList<TextHit> Hits, bool Truncated)> SearchAsync(
        Solution solution, string query, int maxResults, CancellationToken ct)
    {
        query = query.Trim();
        if (query.Length == 0 || maxResults <= 0)
            return ([], false);

        var files = await SolutionFileIndex.FilesAsync(solution, ct);

        // One extra hit distinguishes "exactly the cap" from "there were more".
        int budget = maxResults + 1;
        var hits = new ConcurrentBag<TextHit>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        int found = 0;

        try
        {
            await Parallel.ForEachAsync(
                files,
                new ParallelOptions { CancellationToken = stop.Token, MaxDegreeOfParallelism = Environment.ProcessorCount },
                async (path, token) =>
                {
                    foreach (var hit in ScanFile(path, query, token))
                    {
                        hits.Add(hit);
                        if (Interlocked.Increment(ref found) >= budget)
                        {
                            await stop.CancelAsync();
                            return;
                        }
                    }
                });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The cap was reached, not the caller cancelling.
        }

        var ordered = hits
            .OrderBy(h => h.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.Line)
            .Take(maxResults)
            .ToList();

        return (ordered, found > maxResults);
    }

    private static IEnumerable<TextHit> ScanFile(string path, string query, CancellationToken ct)
    {
        if (SearchFileRules.IsBinaryAsset(path))
            yield break;

        IEnumerable<string> lines;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes || LooksBinary(path))
                yield break;

            lines = File.ReadLines(path);
        }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }

        int lineNumber = 0;
        foreach (string line in Guarded(lines))
        {
            ct.ThrowIfCancellationRequested();

            int column = line.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (column >= 0)
                yield return new TextHit(path, lineNumber, column, Clip(line, column));

            lineNumber++;
        }
    }

    /// <summary>An unreadable file mid-iteration ends the file, not the search.</summary>
    private static IEnumerable<string> Guarded(IEnumerable<string> lines)
    {
        using var enumerator = lines.GetEnumerator();
        while (true)
        {
            try
            {
                if (!enumerator.MoveNext())
                    yield break;
            }
            catch (IOException) { yield break; }

            yield return enumerator.Current;
        }
    }

    /// <summary>A NUL in the first block is the classic text/binary test, and cheap.</summary>
    private static bool LooksBinary(string path)
    {
        Span<byte> probe = stackalloc byte[512];
        using var stream = File.OpenRead(path);
        int read = stream.Read(probe);

        // UTF-16 text is NUL bytes every other position; the BOM vouches for it before the
        // probe would condemn it, and File.ReadLines decodes it fine.
        if (read >= 2 && ((probe[0] == 0xFF && probe[1] == 0xFE) || (probe[0] == 0xFE && probe[1] == 0xFF)))
            return false;

        return probe[..read].IndexOf((byte)0) >= 0;
    }

    /// <summary>
    /// The matched line, trimmed and clipped around the match so a minified single-line file
    /// cannot push kilobytes into every result row.
    /// </summary>
    private static string Clip(string line, int column)
    {
        string trimmed = line.TrimEnd();
        if (trimmed.Length <= MaxLineLength)
            return trimmed;

        int start = Math.Max(0, column - MaxLineLength / 4);
        int length = Math.Min(MaxLineLength, trimmed.Length - start);
        return (start > 0 ? "…" : "") + trimmed.Substring(start, length) + "…";
    }
}

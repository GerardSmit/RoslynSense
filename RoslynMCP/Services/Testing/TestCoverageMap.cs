using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RoslynMCP.Services.Testing;

/// <summary>
/// Which tests execute which lines — the thing a Cobertura report cannot say, because it merges
/// every test's hits into one number per line.
/// </summary>
/// <remarks>
/// Built by running coverage once per test class (see <see cref="TestCoverageMapBuilder"/>), so
/// the unit of attribution is the class rather than the individual test: a per-test map would
/// cost one <c>dotnet test</c> process per test method, which no repository can afford to
/// refresh. The tests inside a class are still listed, so a caller can run and count them
/// individually — only the "which lines did it touch" answer is shared across the class.
/// </remarks>
public sealed record TestCoverageMap(
    string SolutionPath,
    DateTime BuiltAtUtc,
    IReadOnlyList<CoverageMapEntry> Entries)
{
    public static TestCoverageMap Empty(string solutionPath) => new(solutionPath, default, []);

    public bool IsEmpty => Entries.Count == 0;

    public int TestCount => Entries.Sum(e => e.Tests.Count);

    /// <summary>
    /// The tests that executed <paramref name="line"/> of <paramref name="filePath"/> the last
    /// time the map was built.
    /// </summary>
    public IReadOnlyList<CoverageMapEntry> EntriesCovering(string filePath, int line) =>
        EntriesCovering(filePath, [new LineRange(line, line)]);

    /// <summary>
    /// The tests that executed any line in <paramref name="ranges"/>. An empty range list asks
    /// "which tests touch this file at all", which is what a file whose lines have moved since
    /// the map was built can honestly be asked.
    /// </summary>
    public IReadOnlyList<CoverageMapEntry> EntriesCovering(
        string filePath, IReadOnlyList<LineRange> ranges)
    {
        var results = new List<CoverageMapEntry>();

        foreach (var entry in Entries)
        {
            var file = entry.FindFile(filePath);
            if (file is null)
                continue;

            if (ranges.Count == 0 || file.IntersectsAny(ranges))
                results.Add(entry);
        }

        return results;
    }

    /// <summary>
    /// True when the file's content has changed since coverage last ran over it, which makes the
    /// recorded line numbers guesses rather than facts.
    /// </summary>
    public bool IsFileStale(string filePath)
    {
        string? recorded = null;
        foreach (var entry in Entries)
        {
            if (entry.FindFile(filePath) is { ContentHash: { Length: > 0 } hash })
            {
                recorded = hash;
                break;
            }
        }

        if (recorded is null)
            return true;

        return !string.Equals(recorded, CoverageMapHash.OfFile(filePath), StringComparison.Ordinal);
    }

    /// <summary>
    /// Every entry that touches one file, paired with what it touched there. Callers asking
    /// about many positions in the same file — a code lens pass over a document — walk the map
    /// once through this rather than once per position.
    /// </summary>
    public IReadOnlyList<(CoverageMapEntry Entry, CoveredFile File)> EntriesForFile(string filePath)
    {
        var results = new List<(CoverageMapEntry, CoveredFile)>();
        foreach (var entry in Entries)
        {
            if (entry.FindFile(filePath) is { } file)
                results.Add((entry, file));
        }
        return results;
    }

    /// <summary>Every file any test in the map touched — the set line-level answers are
    /// available for.</summary>
    public IReadOnlyCollection<string> CoveredFiles()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries)
            foreach (var file in entry.Files)
                files.Add(file.FilePath);
        return files;
    }
}

/// <summary>One test class's coverage: what it contains, and what it ran.</summary>
public sealed record CoverageMapEntry(
    string ClassFullName,
    string ProjectPath,
    IReadOnlyList<string> Tests,
    IReadOnlyList<CoveredFile> Files,
    /// <summary>The class's own source file and its hash when the entry was recorded, so a
    /// rebuild can skip classes nobody has edited.</summary>
    string? SourceFilePath = null,
    string? SourceHash = null)
{
    public CoveredFile? FindFile(string filePath)
    {
        foreach (var file in Files)
        {
            if (string.Equals(file.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                return file;
        }
        return null;
    }
}

/// <summary>
/// The lines of one file a test class executed, as a flattened list of inclusive
/// <c>[start, end, start, end, …]</c> pairs.
/// </summary>
/// <remarks>
/// Ranges rather than a line list: a single test class routinely covers thousands of contiguous
/// lines, and the map for a real solution is read on every code lens.
/// </remarks>
public sealed record CoveredFile(string FilePath, string? ContentHash, IReadOnlyList<int> Ranges)
{
    public static CoveredFile FromLines(string filePath, string? contentHash, IEnumerable<int> lines)
    {
        var sorted = lines.Distinct().Order().ToList();
        var flat = new List<int>();

        for (int i = 0; i < sorted.Count;)
        {
            int start = sorted[i];
            int end = start;
            while (i + 1 < sorted.Count && sorted[i + 1] == end + 1)
            {
                end = sorted[++i];
            }
            i++;
            flat.Add(start);
            flat.Add(end);
        }

        return new CoveredFile(filePath, contentHash, flat);
    }

    public bool Covers(int line)
    {
        for (int i = 0; i + 1 < Ranges.Count; i += 2)
        {
            if (line >= Ranges[i] && line <= Ranges[i + 1])
                return true;
        }
        return false;
    }

    public bool IntersectsAny(IReadOnlyList<LineRange> ranges)
    {
        for (int i = 0; i + 1 < Ranges.Count; i += 2)
        {
            foreach (var range in ranges)
            {
                if (range.Start <= Ranges[i + 1] && range.End >= Ranges[i])
                    return true;
            }
        }
        return false;
    }

    public int LineCount
    {
        get
        {
            int total = 0;
            for (int i = 0; i + 1 < Ranges.Count; i += 2)
                total += Ranges[i + 1] - Ranges[i] + 1;
            return total;
        }
    }
}

/// <summary>How a file's content is fingerprinted for staleness checks.</summary>
public static class CoverageMapHash
{
    /// <summary>
    /// Line endings are normalized out: a checkout that converts CRLF to LF must not invalidate
    /// every entry in the map.
    /// </summary>
    public static string? OfFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            string text = File.ReadAllText(path).Replace("\r\n", "\n");
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

/// <summary>
/// Where the map lives between processes: the MCP tool builds it, the language server reads it
/// on every code lens, and a chat asking "what covers this" is a third process again. Scoped per
/// solution in the user's temp directory, exactly like <see cref="TestRunStore"/>.
/// </summary>
public static class TestCoverageMapStore
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly object s_lock = new();
    private static string? s_cachedFile;
    private static DateTime s_cachedStamp;
    private static TestCoverageMap? s_cached;

    public static void Save(string solutionPath, TestCoverageMap map)
    {
        string file = FileFor(solutionPath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(map, s_json));

        lock (s_lock)
        {
            s_cachedFile = file;
            s_cached = map;
            s_cachedStamp = LastWrite(file);
        }
    }

    /// <summary>
    /// The stored map, or an empty one. Re-read only when the file's timestamp moves — a code
    /// lens pass asks for this once per method.
    /// </summary>
    public static TestCoverageMap Load(string solutionPath)
    {
        string file = FileFor(solutionPath);

        try
        {
            if (!File.Exists(file))
                return TestCoverageMap.Empty(solutionPath);

            var stamp = LastWrite(file);

            lock (s_lock)
            {
                if (s_cached is not null
                    && string.Equals(s_cachedFile, file, StringComparison.OrdinalIgnoreCase)
                    && s_cachedStamp == stamp)
                {
                    return s_cached;
                }
            }

            var map = JsonSerializer.Deserialize<TestCoverageMap>(File.ReadAllText(file), s_json)
                ?? TestCoverageMap.Empty(solutionPath);

            lock (s_lock)
            {
                s_cachedFile = file;
                s_cachedStamp = stamp;
                s_cached = map;
            }

            return map;
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"Could not read the test coverage map: {ex.Message}", key: "coverage-map");
            return TestCoverageMap.Empty(solutionPath);
        }
    }

    /// <summary>The map for the solution nearest an anchor path, for callers that only know a
    /// file they are looking at.</summary>
    public static TestCoverageMap LoadNearest(string anchorPath) =>
        PathHelper.FindNearestSolution(anchorPath) is { } solution
            ? Load(solution)
            : TestCoverageMap.Empty(anchorPath);

    public static void Clear(string solutionPath)
    {
        try
        {
            string file = FileFor(solutionPath);
            if (File.Exists(file))
                File.Delete(file);
        }
        catch { /* best effort */ }

        lock (s_lock)
        {
            s_cached = null;
            s_cachedFile = null;
        }
    }

    /// <summary>Drops the in-process cache without touching the file — for tests, which write
    /// the file behind this store's back.</summary>
    internal static void ResetCache()
    {
        lock (s_lock)
        {
            s_cached = null;
            s_cachedFile = null;
        }
    }

    private static DateTime LastWrite(string file)
    {
        try { return File.GetLastWriteTimeUtc(file); }
        catch { return DateTime.MinValue; }
    }

    private static string FileFor(string solutionPath) =>
        Path.Combine(
            Path.GetTempPath(), "roslyn-sense", "coverage-map",
            Daemon.HostPaths.SolutionHash(Path.GetFullPath(solutionPath)) + ".json");
}

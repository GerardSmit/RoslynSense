using System.Collections.Concurrent;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Language.Xml;
using RoslynMCP.Services;
using XmlChangeRange = Microsoft.Language.Xml.TextChangeRange;
using XmlSpan = Microsoft.Language.Xml.TextSpan;

namespace RoslynMCP.Languages.Dbml.Core;

/// <summary>One <c>.dbml</c> as the editor sees it: the buffer, and the model read from it.</summary>
/// <param name="Root">The syntax tree the model was read from. Carried because completion asks
/// questions the model cannot answer — what the half-typed element around the caret is, and what its
/// attributes say — and a second parse to ask them would throw away the whole point of the cache.</param>
internal sealed record DbmlDocument(
    string FilePath, SourceText Text, DbmlDatabase Database, XmlDocumentSyntax Root);

/// <summary>
/// Resolves a <c>.dbml</c> path to a parsed model, reusing the previous parse wherever the buffer did
/// not move.
/// </summary>
/// <remarks>
/// The same two levels of reuse as <c>MsBuildDocumentCache</c>, and for the same reason: several
/// providers fire for one keystroke, so a checksum hit has to return the same tree rather than
/// re-reading it per feature, and a real edit splices into the previous tree instead of reparsing the
/// file. The carry-over guard is the same too — <see cref="SourceText.GetChangeRanges"/> only relates
/// texts in one lineage, so a disk read misses the guard and reparses whole, which is correct rather
/// than merely safe.
/// </remarks>
internal static class DbmlDocumentCache
{
    private sealed record Entry(SourceText Text, XmlDocumentSyntax Root, DbmlDatabase Database);

    private static readonly ConcurrentDictionary<string, Entry> s_cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How many times a model was built, split by how much of the tree had to be built.
    /// </summary>
    /// <remarks>
    /// Exposed for tests, which assert on what was <em>reused</em>: a cache that reparsed the world
    /// every time returns exactly the same model, so counting the work is the only way to pin the
    /// behaviour.
    /// </remarks>
    internal static long FullParses;

    /// <inheritdoc cref="FullParses"/>
    internal static long IncrementalParses;

    public static bool IsDbmlFile(string? filePath) =>
        filePath is { Length: > 0 }
        && Path.GetExtension(filePath).Equals(".dbml", StringComparison.OrdinalIgnoreCase);

    public static DbmlDocument? Get(string filePath)
    {
        if (!IsDbmlFile(filePath))
            return null;

        string path = Normalize(filePath);
        return Read(path) is { } text ? For(path, text) : null;
    }

    /// <summary>The model for text the caller already has — the buffer on an LSP request.</summary>
    public static DbmlDocument For(string filePath, SourceText text)
    {
        string path = Normalize(filePath);
        var entry = Parse(path, text);
        return new DbmlDocument(path, text, entry.Database, entry.Root);
    }

    private static Entry Parse(string path, SourceText text)
    {
        if (s_cache.TryGetValue(path, out var cached))
        {
            if (cached.Text.GetChecksum().SequenceEqual(text.GetChecksum()))
                return cached;

            if (Changes(cached.Text, text) is { Length: > 0 } changes)
            {
                Interlocked.Increment(ref IncrementalParses);
                var spliced = Parser.ParseIncremental(text.ToString(), changes, cached.Root);
                var incremental = new Entry(text, spliced, DbmlReader.Read(spliced));
                s_cache[path] = incremental;
                return incremental;
            }
        }

        Interlocked.Increment(ref FullParses);
        var root = Parser.ParseText(text.ToString());
        var entry = new Entry(text, root, DbmlReader.Read(root));
        s_cache[path] = entry;
        return entry;
    }

    /// <inheritdoc cref="MsBuild.Core.MsBuildDocumentCache"/>
    private static XmlChangeRange[] Changes(SourceText before, SourceText after)
    {
        try
        {
            var ranges = after.GetChangeRanges(before);
            if (ranges.Count == 0)
                return [];

            // The whole document is not worth splicing, and is what GetChangeRanges answers
            // conservatively for texts that merely happen to differ.
            if (ranges.Count == 1 && ranges[0].Span.Start == 0 && ranges[0].Span.Length == before.Length)
                return [];

            return [.. ranges.Select(r => new XmlChangeRange(
                new XmlSpan(r.Span.Start, r.Span.Length), r.NewLength))];
        }
        catch (ArgumentException)
        {
            return [];
        }
    }

    private static SourceText? Read(string path)
    {
        if (OpenDocumentStore.TryGet(path, out var open))
            return open;

        try
        {
            if (!File.Exists(path))
                return null;

            using var stream = File.OpenRead(path);
            return SourceText.From(stream);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Drops one file's model — used when it changes on disk under us.</summary>
    public static void Invalidate(string filePath) => s_cache.TryRemove(Normalize(filePath), out _);

    internal static void Clear()
    {
        s_cache.Clear();
        Interlocked.Exchange(ref FullParses, 0);
        Interlocked.Exchange(ref IncrementalParses, 0);
    }

    internal static string Normalize(string filePath)
    {
        try
        {
            return PathHelper.NormalizePath(filePath);
        }
        catch (ArgumentException)
        {
            return filePath;
        }
        catch (IOException)
        {
            return filePath;
        }
    }
}

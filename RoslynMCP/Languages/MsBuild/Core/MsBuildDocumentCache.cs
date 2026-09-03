using System.Collections.Concurrent;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Language.Xml;
using RoslynMCP.Services;
using XmlChangeRange = Microsoft.Language.Xml.TextChangeRange;
using XmlSpan = Microsoft.Language.Xml.TextSpan;

namespace RoslynMCP.Languages.MsBuild.Core;

/// <summary>One project file as the editor sees it: the buffer, and the tree parsed from it.</summary>
internal sealed record MsBuildDocument(string FilePath, SourceText Text, XmlDocumentSyntax Root)
{
    public MsBuildFileKind Kind { get; } = MsBuildFile.KindOf(FilePath);

    public MsBuildFlavour Flavour { get; } = MsBuildFile.FlavourOf(FilePath);
}

/// <summary>
/// Resolves a project-file path to a parsed document, reusing the previous parse wherever the
/// buffer did not move.
/// </summary>
/// <remarks>
/// <para>
/// Every provider in the pack reads through here, so one keystroke costs one parse rather than one
/// per feature that wants to look at the file.
/// </para>
/// <para>
/// Two levels of reuse, and the second is the one that matters on a big file. The cache is keyed on
/// <see cref="SourceText.GetChecksum"/>, so a request for text that has not changed returns the same
/// tree object — that covers the several providers that fire for a single keystroke. When the text
/// <em>has</em> changed, the previous tree is not thrown away: the parser can splice a re-parse of
/// the edited region into it, so typing a character in a 400-line <c>Directory.Packages.props</c>
/// costs the nodes around the caret rather than the file.
/// </para>
/// <para>
/// The carry-over is guarded, the way the markup index guards its own. Reusing a tree is only sound
/// when the previous text is genuinely the text this one was edited from, and
/// <see cref="SourceText.GetChangeRanges"/> only knows that for texts in the same lineage — an
/// editor buffer and its successor. A disk read has no such relationship, so it simply misses the
/// guard and reparses whole, which is correct rather than merely safe: a file changed on disk under
/// us may have moved arbitrarily.
/// </para>
/// </remarks>
internal static class MsBuildDocumentCache
{
    private sealed record Entry(SourceText Text, XmlDocumentSyntax Root);

    private static readonly ConcurrentDictionary<string, Entry> s_cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How many times a tree was built, split by how much of it had to be built.
    /// </summary>
    /// <remarks>
    /// Exposed for tests, which assert on what was <em>reused</em>. Asserting on the returned tree
    /// says nothing — a cache that reparsed the world every time returns exactly the same document —
    /// so the only way to pin the behaviour is to count the work.
    /// </remarks>
    internal static long FullParses;

    /// <inheritdoc cref="FullParses"/>
    internal static long IncrementalParses;

    public static MsBuildDocument? Get(string filePath)
    {
        if (MsBuildFile.KindOf(filePath) is MsBuildFileKind.None)
            return null;

        string path = Normalize(filePath);
        return Read(path) is { } text ? new MsBuildDocument(path, text, Parse(path, text)) : null;
    }

    /// <summary>The tree for text the caller already has — the buffer on an LSP request.</summary>
    public static MsBuildDocument For(string filePath, SourceText text)
    {
        string path = Normalize(filePath);
        return new MsBuildDocument(path, text, Parse(path, text));
    }

    private static XmlDocumentSyntax Parse(string path, SourceText text)
    {
        if (s_cache.TryGetValue(path, out var cached))
        {
            if (cached.Text.GetChecksum().SequenceEqual(text.GetChecksum()))
                return cached.Root;

            if (Changes(cached.Text, text) is { Length: > 0 } changes)
            {
                Interlocked.Increment(ref IncrementalParses);
                var spliced = Parser.ParseIncremental(text.ToString(), changes, cached.Root);
                s_cache[path] = new Entry(text, spliced);
                return spliced;
            }
        }

        Interlocked.Increment(ref FullParses);
        var root = Parser.ParseText(text.ToString());
        s_cache[path] = new Entry(text, root);
        return root;
    }

    /// <summary>
    /// What moved between two texts, or empty when that cannot be known.
    /// </summary>
    /// <remarks>
    /// <see cref="SourceText.GetChangeRanges"/> answers cheaply for texts in one lineage and
    /// conservatively — the whole document — for texts that merely happen to differ. The whole
    /// document is not worth splicing, so it is treated as no answer and the caller reparses.
    /// </remarks>
    private static XmlChangeRange[] Changes(SourceText before, SourceText after)
    {
        try
        {
            var ranges = after.GetChangeRanges(before);
            if (ranges.Count == 0)
                return [];

            if (ranges.Count == 1 && ranges[0].Span.Start == 0 && ranges[0].Span.Length == before.Length)
                return [];

            return [.. ranges.Select(r => new XmlChangeRange(
                new XmlSpan(r.Span.Start, r.Span.Length), r.NewLength))];
        }
        catch (ArgumentException)
        {
            // Texts with no common ancestor; nothing to carry over.
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

    /// <summary>Drops one file's tree — used when it changes on disk under us.</summary>
    public static void Invalidate(string filePath) => s_cache.TryRemove(Normalize(filePath), out _);

    internal static void Clear()
    {
        s_cache.Clear();
        Interlocked.Exchange(ref FullParses, 0);
        Interlocked.Exchange(ref IncrementalParses, 0);
    }

    private static string Normalize(string filePath)
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

using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace RoslynMCP.Debugger;

/// <summary>
/// Whether a file on disk is the file a PDB was built from.
/// </summary>
/// <remarks>
/// <para>
/// Every PDB records a hash of each source document it describes. That hash is the only thing that
/// can tell a genuine match from a coincidence of file names, which is what makes it worth the
/// read: a build from CI or a container writes document paths that exist nowhere on this machine,
/// and matching those by name alone would happily bind a breakpoint into a different copy of
/// <c>Program.cs</c>.
/// </para>
/// <para>
/// The tolerances are not optional. A checkout normalises line endings and a text editor adds or
/// removes a byte-order mark, neither of which changes a single character of the program, and both
/// of which change the hash. Without retrying those variants this would reject nearly every real
/// working copy — which is worse than not checking, because it would be confidently wrong.
/// </para>
/// </remarks>
public static class SourceChecksum
{
    // The algorithm ids PDBs use, from the portable PDB specification. Windows PDBs report the
    // same ones through ISymUnmanagedDocument::GetCheckSumAlgorithmId.
    private static readonly Guid Sha1 = new("ff1816ec-aa5e-4d10-87f7-6f4963833460");
    private static readonly Guid Sha256 = new("8829d00f-11b8-4213-878b-770e8597ac16");
    private static readonly Guid Md5 = new("406ea660-64cf-4c82-b6f0-42d48172a799");

    /// <summary>Source files large enough that they are certainly not the one being looked for.
    /// A bound keeps a mistargeted probe from reading something enormous off disk.</summary>
    private const long MaxSourceBytes = 32 * 1024 * 1024;

    /// <summary>
    /// Whether the file at <paramref name="path"/> hashes to what the PDB recorded.
    /// </summary>
    /// <returns>
    /// False when it does not, and also when the question cannot be answered — an unreadable file,
    /// an algorithm this does not know, or a PDB that recorded no hash at all. Callers use this to
    /// accept a match, never to reject one, so "unknown" has to read as "not confirmed".
    /// </returns>
    public static bool Matches(string path, Guid algorithm, byte[]? expected)
    {
        if (expected is not { Length: > 0 } || path.Length == 0)
            return false;

        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxSourceBytes)
                return false;
        }
        catch
        {
            return false;
        }

        // Asked repeatedly for the same file: a breakpoint is re-bound on every module load and
        // after every applied edit, against every module that has a document of that name. Without
        // this, setting one breakpoint in a large process reads the same source off disk dozens of
        // times and hashes it four ways each time.
        var key = new Answer(
            path, info.LastWriteTimeUtc.Ticks, info.Length, algorithm, Convert.ToHexString(expected));
        if (Answers.TryGetValue(key, out bool remembered))
            return remembered;

        bool answer = Compute(path, algorithm, expected);
        // Bounded rather than evicted: the keys are one per (file, build) pair a session actually
        // asked about, and a debug session that saw a hundred thousand of those has other problems.
        if (Answers.Count < MaxRemembered)
            Answers[key] = answer;

        return answer;
    }

    private const int MaxRemembered = 100_000;

    private readonly record struct Answer(
        string Path, long Ticks, long Length, Guid Algorithm, string Expected);

    private static readonly ConcurrentDictionary<Answer, bool> Answers = new();

    private static bool Compute(string path, Guid algorithm, byte[] expected)
    {
        byte[] content;
        try
        {
            content = File.ReadAllBytes(path);
        }
        catch
        {
            return false;
        }

        foreach (var variant in Variants(content))
        {
            if (HashOf(variant, algorithm, expected.Length) is { } actual &&
                actual.AsSpan().SequenceEqual(expected))
            {
                return true;
            }
        }

        return false;
    }

    /// <remarks>
    /// A PDB can record a hash without naming an algorithm this knows. The length of what it
    /// recorded is enough to tell the three apart, and guessing wrong costs one comparison that
    /// fails rather than a wrong answer.
    /// </remarks>
    private static byte[]? HashOf(byte[] content, Guid algorithm, int expectedLength)
    {
        if (algorithm == Sha256 || (algorithm == Guid.Empty && expectedLength == 32))
            return SHA256.HashData(content);
        if (algorithm == Sha1 || (algorithm == Guid.Empty && expectedLength == 20))
            return SHA1.HashData(content);
        if (algorithm == Md5 || (algorithm == Guid.Empty && expectedLength == 16))
            return MD5.HashData(content);

        return null;
    }

    /// <summary>
    /// The same text as the build could have seen it: as stored, with either line ending, and with
    /// or without a byte-order mark.
    /// </summary>
    /// <remarks>
    /// Ordered cheapest-first, and deduplicated by construction — a file that is already LF-only
    /// yields its LF variant once, not twice — so the common case is one hash of one array.
    /// </remarks>
    private static IEnumerable<byte[]> Variants(byte[] content)
    {
        foreach (var text in LineEndingVariants(content))
        {
            yield return text;
            if (StripBom(text) is { } stripped)
                yield return stripped;
            else
                yield return WithBom(text);
        }
    }

    private static IEnumerable<byte[]> LineEndingVariants(byte[] content)
    {
        yield return content;

        var toLf = ToLf(content);
        if (!ReferenceEquals(toLf, content))
            yield return toLf;

        var toCrLf = ToCrLf(toLf);
        if (!ReferenceEquals(toCrLf, toLf) && !toCrLf.AsSpan().SequenceEqual(content))
            yield return toCrLf;
    }

    private static byte[] ToLf(byte[] content)
    {
        int carriageReturns = 0;
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] == (byte)'\r' && i + 1 < content.Length && content[i + 1] == (byte)'\n')
                carriageReturns++;
        }

        if (carriageReturns == 0)
            return content;

        var result = new byte[content.Length - carriageReturns];
        int at = 0;
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] == (byte)'\r' && i + 1 < content.Length && content[i + 1] == (byte)'\n')
                continue;
            result[at++] = content[i];
        }

        return result;
    }

    private static byte[] ToCrLf(byte[] content)
    {
        int lineFeeds = 0;
        foreach (byte b in content)
        {
            if (b == (byte)'\n')
                lineFeeds++;
        }

        if (lineFeeds == 0)
            return content;

        var result = new byte[content.Length + lineFeeds];
        int at = 0;
        foreach (byte b in content)
        {
            if (b == (byte)'\n')
                result[at++] = (byte)'\r';
            result[at++] = b;
        }

        return result;
    }

    private static byte[]? StripBom(byte[] content) =>
        content is [0xEF, 0xBB, 0xBF, ..] ? content[3..] : null;

    private static byte[] WithBom(byte[] content)
    {
        var result = new byte[content.Length + 3];
        result[0] = 0xEF;
        result[1] = 0xBB;
        result[2] = 0xBF;
        content.CopyTo(result, 3);
        return result;
    }
}

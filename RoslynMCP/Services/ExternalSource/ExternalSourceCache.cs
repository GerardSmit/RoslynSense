using System.Security.Cryptography;
using System.Text;

namespace RoslynMCP.Services.ExternalSource;

/// <summary>
/// The on-disk homes for source that came from outside the solution, and the rules every one of
/// them follows.
/// </summary>
/// <remarks>
/// <para>
/// Four roots rather than one because they expire differently: a decompilation is derived from an
/// assembly and can be regenerated at will, a Source Link file is keyed by a URL containing a
/// commit and never changes, a symbol-server PDB is large and worth an eviction policy, and a
/// reference-source file is keyed by a commit that is immutable by construction. What they share
/// is that nothing under any of them is the user's, which is what <see cref="IsExternalSourcePath"/>
/// is asked about — diagnostics are suppressed and edits are refused for all four alike.
/// </para>
/// </remarks>
internal static class ExternalSourceCache
{
    private static readonly string s_root = Path.Combine(Path.GetTempPath(), "RoslynMCP");

    /// <summary>Files fetched from a Source Link map. Keyed by URL, so keyed by commit.</summary>
    public static string SourceLinkDirectory { get; } = Path.Combine(s_root, "SourceLink");

    /// <summary>Files extracted from a PDB that carries its own sources.</summary>
    public static string EmbeddedDirectory { get; } = Path.Combine(s_root, "EmbeddedSource");

    /// <summary>Files fetched from <c>microsoft/referencesource</c> at a pinned commit.</summary>
    public static string ReferenceSourceDirectory { get; } = Path.Combine(s_root, "ReferenceSource");

    /// <summary>Portable PDBs downloaded from a symbol server, keyed by their SSQP identity.</summary>
    public static string SymbolDirectory { get; } = Path.Combine(s_root, "Symbols");

    /// <summary>Decompiled output. Owned by <see cref="DecompiledSourceService"/>.</summary>
    public static string DecompiledDirectory { get; } = Path.Combine(s_root, "Decompiled");

    private static readonly string[] s_sourceRoots =
    [
        SourceLinkDirectory, EmbeddedDirectory, ReferenceSourceDirectory, DecompiledDirectory,
    ];

    /// <summary>
    /// Whether a path is a file we produced or fetched rather than one the user owns. True for
    /// every source root, including the decompiled one.
    /// </summary>
    public static bool IsExternalSourcePath(string? path)
    {
        if (path is not { Length: > 0 })
            return false;

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        // The separator keeps a sibling like "...\DecompiledExtra" from matching by prefix.
        foreach (string root in s_sourceRoots)
        {
            if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Writes a cache entry and marks it read-only, so an editor that opens it cannot produce an
    /// edit that the next fetch silently discards.
    /// </summary>
    /// <remarks>
    /// Written to a sibling <c>.partial</c> and moved into place, because a half-written file that
    /// parses is worse than no file: the reference-source path establishes correctness by parsing
    /// what it downloaded, and a truncated file can still parse.
    /// </remarks>
    /// <returns>Whether the entry is now on disk.</returns>
    public static bool WriteReadOnly(string target, ReadOnlySpan<byte> content)
    {
        string partial = target + ".partial";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                stream.Write(content);

            // The destination is read-only when it already exists, and Move will not overwrite it.
            ClearReadOnly(target);
            File.Move(partial, target, overwrite: true);
            File.SetAttributes(target, FileAttributes.ReadOnly);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ServiceLog.Warn(
                $"Could not cache external source at '{target}': {ex.Message}",
                key: $"external-cache-write:{target}");
            TryDelete(partial);

            // Another navigation racing us to the same entry is a win, not a failure.
            return File.Exists(target);
        }
    }

    /// <summary>Makes a cache entry writable again so it can be replaced.</summary>
    public static void ClearReadOnly(string path)
    {
        try
        {
            if (File.Exists(path))
                File.SetAttributes(path, FileAttributes.Normal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; the caller's write will report the real problem.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                ClearReadOnly(path);
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover .partial is harmless — the next write overwrites it.
        }
    }

    /// <summary>A short, path-safe digest of a cache key such as a URL or an assembly path.</summary>
    public static string Fingerprint(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];

    /// <summary>
    /// Replaces only the characters a file name may not contain, keeping the extension. What is
    /// cached is opened in an editor, and a file that has lost its <c>.cs</c> is a file the editor
    /// shows as plain text.
    /// </summary>
    public static string SanitizeFileName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (char c in name)
            builder.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);

        return builder.ToString();
    }

    /// <summary>
    /// Keeps the total size of the symbol cache in hand.
    /// </summary>
    /// <remarks>
    /// A single framework PDB is tens of megabytes, so this is the one root that grows fast enough
    /// to matter. Entries are keyed by an identity that never changes, so the oldest-used is always
    /// safe to drop: the worst case is downloading it again.
    /// </remarks>
    public static void PruneSymbols(long maxBytes)
    {
        try
        {
            if (!Directory.Exists(SymbolDirectory))
                return;

            var entries = Directory.EnumerateDirectories(SymbolDirectory)
                .Select(directory => new DirectoryInfo(directory))
                .Select(directory => (
                    Directory: directory,
                    Size: directory.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length),
                    Used: directory.EnumerateFiles("*", SearchOption.AllDirectories)
                        .Select(f => f.LastAccessTimeUtc)
                        .DefaultIfEmpty(directory.CreationTimeUtc)
                        .Max()))
                .OrderByDescending(entry => entry.Used)
                .ToList();

            long kept = 0;
            foreach (var entry in entries)
            {
                kept += entry.Size;
                if (kept <= maxBytes)
                    continue;

                foreach (var file in entry.Directory.EnumerateFiles("*", SearchOption.AllDirectories))
                    ClearReadOnly(file.FullName);

                entry.Directory.Delete(recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cache that cannot be pruned is a disk-space problem, not a correctness one.
        }
    }

    /// <summary>Replaces the characters a path segment may not contain, including dots.</summary>
    public static string SanitizePathSegment(string segment)
    {
        var builder = new StringBuilder(segment.Length);
        foreach (char c in segment)
            builder.Append(c is '.' || Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);

        return builder.ToString();
    }
}

using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace RoslynMCP.Services;

/// <summary>
/// Hashes an assembly by what a compilation loading it would observe, leaving out the fields a
/// deterministic compiler derives from where it was built rather than from what was built.
/// </summary>
/// <remarks>
/// <para>
/// A deterministic build embeds the PDB path in the image's CodeView debug entry, and then
/// derives the module version id, the PDB id, the PDB checksum and the COFF timestamp from a hash
/// of the image — path included. The same analyzer built from <c>d:\src\...</c> (the spelling VS
/// Code hands the extension, and so the one this process builds with) and from <c>D:\src\...</c>
/// (Visual Studio, a terminal) is therefore two byte-different files of the same size, differing
/// in nothing that changes a diagnostic. Hashing whole files made every one of those builds a
/// "rebuild" of the analyzer: the analyzer context was evicted, every consumer's semantic version
/// bumped, and every analyzer result in the solution recomputed — the stall after every build.
/// </para>
/// <para>
/// What is masked is exactly that set: the COFF timestamp, the optional header's checksum, the
/// debug directory and each entry's data (CodeView path and id, PDB checksum, embedded PDB), and
/// the module version id in the GUID heap. Everything else — IL, metadata, resources, strong name
/// — is hashed as-is, so a real change to the analyzer still reads as one. A file that is not a
/// PE image, or is unreadable as one, is hashed byte for byte.
/// </para>
/// </remarks>
internal static class AssemblyContentFingerprint
{
    private const int GuidSize = 16;

    /// <summary>
    /// Feeds <paramref name="stream"/> into <paramref name="hash"/> with the volatile fields
    /// zeroed. The stream must be seekable; it is read from the start.
    /// </summary>
    public static void Append(IncrementalHash hash, Stream stream)
    {
        var masked = VolatileRanges(stream);
        stream.Position = 0;

        var buffer = new byte[81920];
        long position = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            foreach (var (offset, length) in masked)
            {
                long from = Math.Max(offset, position);
                long to = Math.Min(offset + length, position + read);
                if (from < to)
                    Array.Clear(buffer, (int)(from - position), (int)(to - from));
            }

            hash.AppendData(buffer, 0, read);
            position += read;
        }
    }

    /// <summary>
    /// The byte ranges of <paramref name="stream"/> that a deterministic compiler derives from
    /// the build's paths, sorted by offset; empty for anything that is not a managed PE image.
    /// </summary>
    internal static List<(long Offset, int Length)> VolatileRanges(Stream stream)
    {
        var ranges = new List<(long Offset, int Length)>();
        stream.Position = 0;

        try
        {
            using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
            if (!pe.HasMetadata)
                return ranges;

            var headers = pe.PEHeaders;

            // COFF header: Machine (2), NumberOfSections (2), then TimeDateStamp (4).
            ranges.Add((headers.CoffHeaderStartOffset + 4, 4));

            if (headers.PEHeader is { } optional)
            {
                // CheckSum sits at the same offset in the PE32 and PE32+ optional headers.
                ranges.Add((headers.PEHeaderStartOffset + 64, 4));

                var debug = optional.DebugTableDirectory;
                if (debug.Size > 0 && headers.TryGetDirectoryOffset(debug, out int table))
                    ranges.Add((table, debug.Size));

                foreach (var entry in pe.ReadDebugDirectory())
                {
                    if (entry.DataSize > 0)
                        ranges.Add((entry.DataPointer, entry.DataSize));
                }
            }

            var metadata = pe.GetMetadataReader();
            var mvid = metadata.GetModuleDefinition().Mvid;

            // The GUID heap is indexed from one. Read back before masking: if the arithmetic ever
            // disagrees with the reader, hashing the id is the safe mistake — a spurious rebuild —
            // and zeroing sixteen bytes of something else is not.
            long mvidOffset = headers.MetadataStartOffset
                + metadata.GetHeapMetadataOffset(HeapIndex.Guid)
                + (long)(MetadataTokens.GetHeapOffset(mvid) - 1) * GuidSize;
            if (ReadsBackAs(stream, mvidOffset, metadata.GetGuid(mvid)))
                ranges.Add((mvidOffset, GuidSize));
        }
        catch (BadImageFormatException)
        {
            // Not a PE image, or a truncated one: the caller hashes it byte for byte.
            ranges.Clear();
        }

        ranges.Sort();
        return ranges;
    }

    private static bool ReadsBackAs(Stream stream, long offset, Guid expected)
    {
        if (offset < 0 || offset + GuidSize > stream.Length)
            return false;

        Span<byte> bytes = stackalloc byte[GuidSize];
        stream.Position = offset;
        stream.ReadExactly(bytes);
        return new Guid(bytes) == expected;
    }
}

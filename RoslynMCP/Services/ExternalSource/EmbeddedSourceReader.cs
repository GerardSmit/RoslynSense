using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection.Metadata;

namespace RoslynMCP.Services.ExternalSource;

/// <summary>
/// Reads source that a PDB carries inside itself, put there by <c>&lt;EmbedAllSources&gt;</c>.
/// </summary>
/// <remarks>
/// The best case of the whole feature: the exact file the assembly was compiled from, already on
/// disk, with no network, no URL to resolve and no host that might be down. It is checked before
/// the Source Link map for that reason, and it is exempt from the feature switches — none of the
/// reasons to turn network lookups off apply to bytes that are already here.
/// </remarks>
internal static class EmbeddedSourceReader
{
    /// <summary>The <c>EmbeddedSource</c> custom debug information kind.</summary>
    private static readonly Guid EmbeddedSourceKind = new("0E8A571B-6926-466E-B4AD-8AB04611F5FE");

    /// <summary>A source file larger than this is not a source file.</summary>
    private const int MaxSourceBytes = 16 * 1024 * 1024;

    /// <summary>
    /// The source text embedded for a document, or null when this PDB does not carry it.
    /// </summary>
    public static byte[]? TryRead(MetadataReader pdb, DocumentHandle document)
    {
        foreach (var handle in pdb.GetCustomDebugInformation(document))
        {
            var information = pdb.GetCustomDebugInformation(handle);
            if (pdb.GetGuid(information.Kind) != EmbeddedSourceKind)
                continue;

            return Decode(pdb.GetBlobBytes(information.Value));
        }

        return null;
    }

    /// <summary>
    /// Unpacks the blob: a four-byte format, then the content. Zero means the bytes follow as they
    /// are; anything larger is the uncompressed size and the rest is a raw Deflate stream.
    /// </summary>
    internal static byte[]? Decode(byte[] blob)
    {
        if (blob.Length < sizeof(int))
            return null;

        int format = BinaryPrimitives.ReadInt32LittleEndian(blob);
        if (format == 0)
            return blob.Length - sizeof(int) > MaxSourceBytes ? null : blob[sizeof(int)..];

        // A negative or implausible size is a malformed blob, not a very large file.
        if (format < 0 || format > MaxSourceBytes)
            return null;

        try
        {
            using var compressed = new MemoryStream(blob, sizeof(int), blob.Length - sizeof(int));
            using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);

            byte[] content = new byte[format];
            deflate.ReadExactly(content);
            return content;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            return null;
        }
    }
}

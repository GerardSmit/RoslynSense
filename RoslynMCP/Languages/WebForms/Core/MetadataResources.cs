using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Resources;

namespace RoslynMCP.Languages.WebForms.Core;

/// <summary>
/// The string table an assembly carries inside itself. <c>System.Web</c> documents its controls
/// with <c>[WebSysDescription("Button_Text")]</c> — a key into the <c>.resources</c> stream
/// compiled into <c>System.Web.dll</c> rather than the sentence itself — so the sentence is
/// reachable only by reading the assembly file.
/// </summary>
/// <remarks>
/// Metadata, never reflection. The assembly whose strings are wanted is a .NET Framework binary
/// that this process cannot load, and even where loading would work it would run an arbitrary
/// module initializer to render a tooltip. <see cref="PEReader"/> locates the resource blob and
/// <see cref="ResourceReader"/> reads the entries out of it, exactly as the runtime would.
/// </remarks>
internal static class MetadataResources
{
    /// <summary>Keyed on the file, which is the identity that matters: every compilation that
    /// references the same <c>System.Web.dll</c> shares one read of it.</summary>
    private static readonly ConcurrentDictionary<string, FrozenDictionary<string, string>> s_tables =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The string <paramref name="key"/> names in the assembly's own resources, or
    /// <c>null</c> when the file, its resources or the key are not there.</summary>
    public static string? Lookup(string assemblyPath, string key) =>
        Table(assemblyPath).TryGetValue(key, out string? value) ? value : null;

    private static FrozenDictionary<string, string> Table(string assemblyPath) =>
        s_tables.GetOrAdd(assemblyPath, static path =>
        {
            try
            {
                return Read(path);
            }
            catch
            {
                // A missing, truncated or unmanaged file is a reason to say nothing about a
                // symbol, not to fail the request that asked about it.
                return FrozenDictionary<string, string>.Empty;
            }
        });

    private static FrozenDictionary<string, string> Read(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);

        if (!pe.HasMetadata || pe.PEHeaders.CorHeader is not { ResourcesDirectory.Size: > 0 } corHeader)
            return FrozenDictionary<string, string>.Empty;

        var metadata = pe.GetMetadataReader();
        var directory = pe.GetSectionData(corHeader.ResourcesDirectory.RelativeVirtualAddress);
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var handle in metadata.ManifestResources)
        {
            var resource = metadata.GetManifestResource(handle);

            // A non-nil implementation puts the bytes in another file of a multi-file assembly
            // or in a referenced one, neither of which is this file's own table.
            if (!resource.Implementation.IsNil
                || !metadata.GetString(resource.Name).EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                int start = (int)resource.Offset;
                if (start < 0 || start + sizeof(int) > directory.Length)
                    continue;

                // Each entry in the directory is its own length-prefixed blob.
                var reader = directory.GetReader(start, directory.Length - start);
                int length = reader.ReadInt32();
                if (length < 0 || length > reader.RemainingBytes)
                    continue;

                using var blob = new MemoryStream(reader.ReadBytes(length), writable: false);
                Add(blob, strings);
            }
            catch
            {
                // One unreadable stream must not cost the assembly its other tables.
            }
        }

        return strings.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static void Add(Stream blob, Dictionary<string, string> strings)
    {
        using var reader = new ResourceReader(blob);
        var entries = reader.GetEnumerator();

        while (entries.MoveNext())
        {
            if (entries.Key is not string key)
                continue;

            try
            {
                if (entries.Value is string text)
                    strings[key] = text;
            }
            catch
            {
                // An entry the runtime refuses to materialize — anything written through
                // BinaryFormatter — costs its own key and no other.
            }
        }
    }
}

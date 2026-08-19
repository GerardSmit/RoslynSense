using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Xml.Linq;

namespace RoslynMCP.Languages.DotSettings.Core;

/// <summary>
/// One entry in a <c>.DotSettings</c> file, with the key already taken apart.
/// </summary>
/// <param name="Path">The key's segments after <c>/Default</c>, joined by <c>/</c> and with the
/// indexed ones removed — <c>CodeInspection/NamespaceProvider/NamespaceFoldersToSkip</c>. This is
/// what a caller matches on; it is stable across the entries of a collection.</param>
/// <param name="Index">The decoded index of an entry in a collection, or null for a scalar. It is
/// where the interesting half of a collection entry lives: the value is almost always just
/// <c>True</c>, and the folder or filter being named is the index.</param>
/// <param name="Accessor">The <c>@</c> suffix — <c>EntryValue</c>, <c>EntryIndexedValue</c>,
/// <c>KeyIndexDefined</c> or <c>EntryIndexRemoved</c>.</param>
internal readonly record struct DotSettingsEntry(
    string Path, string? Index, string Accessor, string? Value)
{
    /// <summary>
    /// Whether this entry adds its index to a collection, as opposed to declaring it or taking it
    /// back out. A removal is written as a real entry rather than an absence, because a stronger
    /// layer has to be able to undo what a weaker one added.
    /// </summary>
    public bool IsPresentIndex =>
        Index is not null
        && Accessor == "EntryIndexedValue"
        && !string.Equals(Value, "False", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this entry removes its index from a collection a weaker layer filled.</summary>
    public bool IsRemovedIndex =>
        Index is not null
        && (Accessor == "EntryIndexRemoved"
            || (Accessor == "EntryIndexedValue"
                && string.Equals(Value, "False", StringComparison.OrdinalIgnoreCase)));
}

/// <summary>One parsed <c>.DotSettings</c> file.</summary>
internal sealed record DotSettingsDocument(string FilePath, ImmutableArray<DotSettingsEntry> Entries);

/// <summary>
/// Reads a <c>.DotSettings</c> file into its entries.
/// </summary>
/// <remarks>
/// The file is a WPF <c>ResourceDictionary</c> — JetBrains reused the XAML resource format rather
/// than inventing one — so every setting is an element whose <c>x:Key</c> is the whole key path and
/// whose text is the value. Nothing here cares about the element's type (<c>s:Boolean</c>,
/// <c>s:String</c>): the accessor at the end of the key already says how to read the value, and the
/// callers all want either a decoded index or a string.
/// </remarks>
internal static class DotSettingsReader
{
    private static readonly XNamespace s_xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    public static ImmutableArray<DotSettingsEntry> Read(string xml)
    {
        XDocument document;

        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            // A malformed layer contributes nothing rather than failing the resolve. These files
            // are merged by hand often enough that a conflict marker must not take the solution
            // down with it.
            return [];
        }

        if (document.Root is null)
            return [];

        var entries = ImmutableArray.CreateBuilder<DotSettingsEntry>();

        foreach (var element in document.Root.Elements())
        {
            if (element.Attribute(s_xaml + "Key")?.Value is not { Length: > 0 } key)
                continue;

            if (Parse(key) is { } parsed)
                entries.Add(parsed with { Value = element.Value });
        }

        return entries.ToImmutable();
    }

    /// <summary>
    /// One key path into its parts. Returns null for a key that is not rooted at <c>/Default</c>,
    /// which is the only root these files use.
    /// </summary>
    internal static DotSettingsEntry? Parse(string key)
    {
        var segments = key.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length < 2 || segments[0] != "Default")
            return null;

        string accessor = "";
        string? index = null;
        var path = new List<string>(segments.Length);

        for (int i = 1; i < segments.Length; i++)
        {
            string segment = segments[i];

            if (segment.StartsWith('@'))
            {
                accessor = segment[1..];
                continue;
            }

            // An index can sit in the middle of a path, not only at its end
            // (/CodeStyle/Generate/=DisposePattern/Options). The last one wins: it is the one the
            // accessor belongs to.
            if (segment.StartsWith('='))
            {
                index = DotSettingsEscaping.Decode(segment[1..]);
                continue;
            }

            path.Add(segment);
        }

        return new DotSettingsEntry(string.Join('/', path), index, accessor, Value: null);
    }
}

/// <summary>
/// Parsed layers, keyed by path and invalidated by write time.
/// </summary>
/// <remarks>
/// A settings layer is read on paths that run per file — the namespace inferred for a new class,
/// the exclusion check a search applies to every candidate — so re-reading and re-parsing the XML
/// each time is not an option. Write time rather than a checksum because these files are not open
/// in the editor: nothing else in the process is holding a newer copy of them.
/// </remarks>
internal static class DotSettingsDocumentCache
{
    private sealed record Entry(DateTime WriteTimeUtc, long Length, DotSettingsDocument Document);

    private static readonly ConcurrentDictionary<string, Entry> s_cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The parsed file, or null when it does not exist or cannot be read.</summary>
    public static DotSettingsDocument? Get(string filePath)
    {
        FileInfo info;

        try
        {
            info = new FileInfo(filePath);
            if (!info.Exists)
            {
                s_cache.TryRemove(filePath, out _);
                return null;
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        if (s_cache.TryGetValue(filePath, out var cached)
            && cached.WriteTimeUtc == info.LastWriteTimeUtc
            && cached.Length == info.Length)
        {
            return cached.Document;
        }

        string xml;

        try
        {
            xml = File.ReadAllText(filePath);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        var document = new DotSettingsDocument(filePath, DotSettingsReader.Read(xml));
        s_cache[filePath] = new Entry(info.LastWriteTimeUtc, info.Length, document);
        return document;
    }

    public static void Invalidate(string filePath) => s_cache.TryRemove(filePath, out _);

    /// <summary>Drops everything. For tests, which write layers into a fresh temp directory.</summary>
    public static void Clear() => s_cache.Clear();
}

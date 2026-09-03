using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMCP.Languages.Resources.Core;

/// <summary>One <c>&lt;data&gt;</c> entry of a <c>.resx</c>, and where it is written.</summary>
/// <param name="Value">Null for an entry that is not a string — a <c>ResXFileRef</c> or a
/// serialized object. The key still counts, so that neither a missing-key diagnostic nor a rename
/// pretends the entry is not there.</param>
/// <param name="KeySpan">The <c>name=</c> attribute's value, quotes excluded — the range a rename
/// replaces. <see langword="default"/> when the name could not be spanned exactly, which is the
/// reader's way of declining the rename.</param>
internal readonly record struct ResourceEntry(
    string Key, string? Value, string? Comment, TextSpan KeySpan, TextSpan ValueSpan);

/// <summary>One <c>.resx</c> as a key table, without its XML.</summary>
internal sealed record ResourceFileIndex
{
    public required string FilePath { get; init; }

    /// <summary>The culture the file name names, or null for the neutral file. Canonicalized
    /// through <see cref="CultureInfo.GetCultureInfo(string)"/>, because DNN lower-cases the name
    /// to look a file up and re-cases it to write one, so both <c>nl-nl</c> and <c>nl-NL</c> occur
    /// on disk for the same culture.</summary>
    public required CultureInfo? Culture { get; init; }

    /// <summary>Higher wins; 0 is the uncustomized file.</summary>
    public required int OverrideRank { get; init; }

    /// <summary>The customization segment as written — <c>Host</c>, <c>Portal-3</c>.</summary>
    public required string? OverrideTag { get; init; }

    /// <summary>Ordinal, not <see cref="StringComparer.OrdinalIgnoreCase"/>:
    /// <c>ResourceManager</c> compares keys case-sensitively, so <c>Title</c> and <c>title</c> are
    /// two resources and folding them would merge entries the runtime keeps apart.</summary>
    public required ImmutableDictionary<string, ResourceEntry> Entries { get; init; }

    /// <summary>Keys the file declares more than once. <see cref="Entries"/> keeps the first.</summary>
    public required ImmutableArray<string> DuplicateKeys { get; init; }

    /// <summary>The file as the grouping pass knows it: named and placed, not yet read.</summary>
    public static ResourceFileIndex Unread(
        string filePath, CultureInfo? culture, int overrideRank, string? overrideTag) =>
        new()
        {
            FilePath = filePath,
            Culture = culture,
            OverrideRank = overrideRank,
            OverrideTag = overrideTag,
            Entries = ImmutableDictionary<string, ResourceEntry>.Empty,
            DuplicateKeys = [],
        };
}

/// <summary>
/// Every <c>.resx</c> sharing a base name in one directory: the neutral file, its translations and
/// its customizations.
/// </summary>
/// <remarks>
/// The family is the unit every feature works against, because the winner is not knowable at edit
/// time — DNN picks one of up to 27 files from the portal id, the thread culture and a
/// database-configured fallback locale, and none of the three exists in an editor. So definition
/// offers the whole family in precedence order, hover reports which members define the key, and a
/// diagnostic fires only when no member does.
/// </remarks>
internal sealed record ResourceFamily
{
    /// <summary>The file name with <c>.resx</c> and every variant segment stripped —
    /// <c>View.ascx</c> for <c>View.ascx.nl-NL.Portal-3.resx</c>.</summary>
    public required string BaseName { get; init; }

    public required string Directory { get; init; }

    /// <summary>Neutral first, then cultures by name, then overrides by rank.</summary>
    public required ImmutableArray<ResourceFileIndex> Files { get; init; }

    /// <summary>The union across the family — completion's source.</summary>
    public required ImmutableArray<string> AllKeys { get; init; }

    /// <summary>Keys no rank-0 file declares. A note, not an error:
    /// <c>TryGetFromResourceFile</c> reads each file directly and never requires the neutral one to
    /// carry the key.</summary>
    public required ImmutableArray<string> OverrideOnlyKeys { get; init; }

    /// <summary>False while the family is still the grouping pass's shell — its key tables are
    /// read on demand, one family at a time, because a multi-portal site has thousands of
    /// files.</summary>
    public required bool KeysLoaded { get; init; }

    /// <summary>The uncustomized, culture-neutral file — what a definition offers first.</summary>
    public ResourceFileIndex? Neutral =>
        Files.FirstOrDefault(f => f is { Culture: null, OverrideRank: 0 });

    /// <summary>A family of translations whose original is not on disk.</summary>
    public bool MissingNeutral => Neutral is null;

    /// <summary>The family as the grouping pass knows it: its members placed, none of them read.</summary>
    public static ResourceFamily Unread(
        string baseName, string directory, ImmutableArray<ResourceFileIndex> files) =>
        new()
        {
            BaseName = baseName,
            Directory = directory,
            Files = files,
            AllKeys = [],
            OverrideOnlyKeys = [],
            KeysLoaded = false,
        };
}

/// <summary>Every resource family a project owns, indexed the two ways a lookup asks for one.</summary>
internal sealed record ResourceCatalog
{
    public static ResourceCatalog Empty { get; } = Create([]);

    public required ImmutableArray<ResourceFamily> Families { get; init; }

    /// <summary>Case-insensitive, and a list rather than a single family: the same base name
    /// occurs once per <c>App_LocalResources</c> folder on a site with many pages.</summary>
    public required ImmutableDictionary<string, ImmutableArray<ResourceFamily>> ByBaseName { get; init; }

    public required ImmutableDictionary<string, ImmutableArray<ResourceFamily>> ByDirectory { get; init; }

    public static ResourceCatalog Create(ImmutableArray<ResourceFamily> families)
    {
        var byBaseName = new Dictionary<string, List<ResourceFamily>>(StringComparer.OrdinalIgnoreCase);
        var byDirectory = new Dictionary<string, List<ResourceFamily>>(StringComparer.OrdinalIgnoreCase);

        foreach (var family in families)
        {
            Bucket(byBaseName, family.BaseName).Add(family);
            Bucket(byDirectory, family.Directory).Add(family);
        }

        return new ResourceCatalog
        {
            Families = families,
            ByBaseName = Freeze(byBaseName),
            ByDirectory = Freeze(byDirectory),
        };
    }

    /// <summary>Every family with this base name, in any directory.</summary>
    public ImmutableArray<ResourceFamily> Named(string baseName) =>
        ByBaseName.TryGetValue(baseName, out var families) ? families : [];

    /// <summary>The one family a directory and base name name together.</summary>
    public ResourceFamily? Find(string directory, string baseName)
    {
        if (!ByDirectory.TryGetValue(directory, out var families))
            return null;

        foreach (var family in families)
        {
            if (family.BaseName.Equals(baseName, StringComparison.OrdinalIgnoreCase))
                return family;
        }

        return null;
    }

    private static List<ResourceFamily> Bucket(
        Dictionary<string, List<ResourceFamily>> index, string key)
    {
        if (!index.TryGetValue(key, out var bucket))
            index[key] = bucket = [];
        return bucket;
    }

    private static ImmutableDictionary<string, ImmutableArray<ResourceFamily>> Freeze(
        Dictionary<string, List<ResourceFamily>> index)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, ImmutableArray<ResourceFamily>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var (key, bucket) in index)
            builder.Add(key, [.. bucket]);

        return builder.ToImmutable();
    }
}

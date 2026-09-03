using System.Collections.Immutable;

namespace RoslynMCP.Languages.Resources.Core;

/// <summary>
/// The family one <c>.resx</c> belongs to, reached from nothing but its path.
/// </summary>
/// <remarks>
/// <see cref="ResourceCatalogService"/> answers "which families does this project have"; an editor
/// request about an open <c>.resx</c> carries a path and no project, and this answers the same
/// question from that. Decomposing the file's own directory is exact rather than approximate — a
/// family never crosses one — and costs a single top-level enumeration instead of a walk from the
/// project root.
/// </remarks>
internal static class ResourceDocuments
{
    /// <summary>
    /// The family <paramref name="filePath"/> is a member of, its key tables not yet read, or null
    /// when the file is gone.
    /// </summary>
    /// <remarks>
    /// Not memoized. The grouping is a function of the directory's file names, so the answer moves
    /// whenever a file is created or deleted — and the enumeration it costs is one directory, which
    /// is what the catalog's own cache exists to avoid paying per <em>project</em>.
    /// </remarks>
    public static ResourceFamily? FamilyOf(
        string filePath, ImmutableArray<ResourceOverrideRule> overrides)
    {
        if (Path.GetDirectoryName(filePath) is not { Length: > 0 } directory)
            return null;

        List<string> siblings;

        try
        {
            siblings = [.. Directory.EnumerateFiles(directory, "*.resx", SearchOption.TopDirectoryOnly)];
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var family in ResourceFamilyParser.Decompose(directory, siblings, overrides))
        {
            if (Member(family, filePath) is not null)
                return family;
        }

        return null;
    }

    /// <summary>The member of <paramref name="family"/> at this path, or null when it has none.</summary>
    public static ResourceFileIndex? Member(ResourceFamily family, string filePath)
    {
        foreach (var file in family.Files)
        {
            if (file.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase))
                return file;
        }

        return null;
    }
}

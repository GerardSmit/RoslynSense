using RoslynMCP.Languages.Resources.Core;

namespace RoslynMCP.Languages.Resources;

internal sealed partial class ResourcesLanguage : ILanguageWatchedFileHandler
{
    /// <summary>
    /// A <c>.resx</c> changed outside the editor. A content edit re-reads the family and keeps the
    /// catalog — membership is a function of file names and none of them moved. A create or delete
    /// moved membership, so every catalog covering the path regroups.
    /// </summary>
    public bool Invalidate(string path, WatchedFileChange change)
    {
        if (!Path.GetExtension(path).Equals(".resx", StringComparison.OrdinalIgnoreCase))
            return false;

        if (change == WatchedFileChange.Changed)
            ResourceCatalogService.InvalidateContent(path);
        else
            ResourceCatalogService.InvalidateLayout(path);
        return true;
    }
}

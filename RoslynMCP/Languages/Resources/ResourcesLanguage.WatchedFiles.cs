using RoslynMCP.Languages.Resources.Core;

namespace RoslynMCP.Languages.Resources;

internal sealed partial class ResourcesLanguage : ILanguageWatchedFileHandler
{
    /// <summary>
    /// A <c>.resx</c> changed outside the editor. The batch this arrives in has already lost which
    /// of created, changed or deleted it was, so every event is treated as a membership change:
    /// regrouping a directory reads no file contents, while assuming a content edit would leave a
    /// deleted file in its family and a created one out of it.
    /// </summary>
    public bool Invalidate(string path)
    {
        if (!Path.GetExtension(path).Equals(".resx", StringComparison.OrdinalIgnoreCase))
            return false;

        ResourceCatalogService.InvalidateLayout(path);
        return true;
    }
}

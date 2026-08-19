using RoslynMCP.Languages.WebConfig.Core;

namespace RoslynMCP.Languages.WebConfig;

internal sealed partial class WebConfigLanguage : ILanguageWatchedFileHandler
{
    /// <summary>
    /// A config file changed under the editor: drop its parse so the next read sees the disk.
    /// Neither index needs anything here — the C# one keys on the project's semantic version, and
    /// the markup one on the shared markup parse, both of which move by themselves.
    /// </summary>
    public bool Invalidate(string path, WatchedFileChange change)
    {
        if (!WebConfigFile.IsConfigPath(path))
            return false;

        WebConfigDocumentCache.Invalidate(path);
        return true;
    }
}

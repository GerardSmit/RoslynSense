using RoslynMCP.Languages.AppSettings.Core;

namespace RoslynMCP.Languages.AppSettings;

internal sealed partial class AppSettingsLanguage : ILanguageWatchedFileHandler
{
    /// <summary>
    /// A configuration file changed under the editor: drop its parse so the next read sees the
    /// disk. The C# side needs nothing here — <see cref="Core.ConfigurationUsageIndex"/> keys on
    /// the project's semantic version, which a C# edit moves by itself.
    /// </summary>
    public bool Invalidate(string path, WatchedFileChange change)
    {
        if (!AppSettingsFile.IsConfigurationPath(path))
            return false;

        AppSettingsDocumentCache.Invalidate(path);
        return true;
    }
}

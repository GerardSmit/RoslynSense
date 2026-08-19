using System.Collections.Immutable;

namespace RoslynMCP.Languages.WebConfig.Core;

/// <summary>
/// The declarations of one setting across an application's config files, weakest first: the
/// application's own <c>web.config</c>, then the ones its subdirectories add.
/// </summary>
/// <remarks>
/// Reversed against the appsettings chain in nothing but its cause. There the later file is an
/// environment overlay chosen at startup; here it is a directory nearer the page being served.
/// Both end with the declaration that wins, which is what lets one renderer describe both.
/// </remarks>
internal static class WebConfigOverrides
{
    public static ImmutableArray<ConfigDeclaration> ChainFor(
        string? projectFilePath, WebConfigSection section, string name)
    {
        if (projectFilePath is not { Length: > 0 })
            return [];

        var chain = ImmutableArray.CreateBuilder<ConfigDeclaration>();

        foreach (string configFile in WebConfigSettings.ConfigFilesFor(projectFilePath))
        {
            if (WebConfigDocumentCache.Get(configFile) is not { } document
                || document.Find(section, name) is not { } entry)
            {
                continue;
            }

            chain.Add(new ConfigDeclaration(
                document.FilePath, Label(projectFilePath, document.FilePath),
                entry.Value ?? entry.Provider,
                WebConfigReferenceService.Location(document, entry)));
        }

        return chain.ToImmutable();
    }

    /// <summary>
    /// A path as the application sees it. Every file in the chain is called <c>web.config</c>, so
    /// the directory is the only thing that tells them apart.
    /// </summary>
    public static string Label(string? projectFilePath, string filePath)
    {
        if (Path.GetDirectoryName(projectFilePath) is not { Length: > 0 } root)
            return Path.GetFileName(filePath);

        return filePath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? filePath[root.Length..].TrimStart(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : Path.GetFileName(filePath);
    }
}

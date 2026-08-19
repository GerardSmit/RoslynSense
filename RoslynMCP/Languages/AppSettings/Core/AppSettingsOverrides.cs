using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp;

namespace RoslynMCP.Languages.AppSettings.Core;

/// <summary>
/// The declarations of one configuration path across the files that feed a project's keyspace,
/// weakest first.
/// </summary>
/// <remarks>
/// The order is the host's probe order — <c>appsettings.json</c>, then the environment overlays,
/// then user secrets — which is the order in which a later file replaces an earlier one. It is
/// what <see cref="AppSettingsWorkspace.ConfigurationFilesFor"/> already returns; this only reads
/// the value out of each file so the difference between two declarations can be shown rather
/// than just their existence.
/// </remarks>
internal static class AppSettingsOverrides
{
    public static ImmutableArray<ConfigDeclaration> ChainFor(string? projectFilePath, string path)
    {
        if (projectFilePath is not { Length: > 0 })
            return [];

        var chain = ImmutableArray.CreateBuilder<ConfigDeclaration>();

        foreach (string configFile in AppSettingsWorkspace.ConfigurationFilesFor(projectFilePath))
        {
            // Sections are left out: two objects declaring the same name are merged key by key,
            // not replaced, so a section is never overridden — only the leaves under it are.
            if (AppSettingsDocumentCache.Get(configFile) is not { } document
                || document.Find(path) is not { } key
                || key.Kind == AppSettingsValueKind.Object)
            {
                continue;
            }

            chain.Add(new ConfigDeclaration(
                document.FilePath, Label(document.FilePath), ValueText(document.Text, key),
                new Lsp.Protocol.Location(
                    LspConverters.PathToUri(document.FilePath),
                    LspConverters.ToRange(document.Text.Lines, key.NameSpan))));
        }

        return chain.ToImmutable();
    }

    /// <summary>
    /// The file as a reader names it. A secrets store is named by what it is rather than by where
    /// it is: its path is a GUID under the user profile, which identifies it to nobody.
    /// </summary>
    private static string Label(string filePath) =>
        AppSettingsFile.IsSecretsPath(filePath) ? "user secrets" : Path.GetFileName(filePath);

    /// <summary>
    /// What the file gives the key, as written. A section has no value to show — the override is
    /// per leaf, and the two objects are merged rather than replaced.
    /// </summary>
    public static string? ValueText(SourceText text, AppSettingsKey key)
    {
        if (key.Kind == AppSettingsValueKind.Object || key.ValueSpan.IsEmpty
            || key.ValueSpan.End > text.Length)
        {
            return null;
        }

        string raw = text.ToString(key.ValueSpan).Trim();

        return key.Kind == AppSettingsValueKind.String && raw.Length >= 2
            && raw[0] == '"' && raw[^1] == '"'
                ? raw[1..^1]
                : raw;
    }
}

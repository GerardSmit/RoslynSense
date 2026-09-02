using Microsoft.Language.Xml;

namespace RoslynMCP.Languages.DotSettings.Core;

/// <summary>
/// Writes back the one <c>.DotSettings</c> setting a user can change from the Solution Explorer:
/// whether a folder contributes a namespace segment.
/// </summary>
/// <remarks>
/// <para>
/// The rest of the pack only reads. This exists because the setting has no other home — it is not
/// an <c>.editorconfig</c> key and not an MSBuild property, so a folder marked "do not create a
/// namespace" can only be un-marked in ReSharper or Rider, or here.
/// </para>
/// <para>
/// The project's team-shared layer is the one written, which is where Rider puts it and where a
/// survey of real solutions found the folder rules living. Through
/// <see cref="Microsoft.Language.Xml"/> rather than XDocument, so a file a team has hand-edited
/// keeps its formatting and the diff is the one line that changed.
/// </para>
/// </remarks>
internal static class DotSettingsWriter
{
    private const string NamespaceFoldersKey =
        "/Default/CodeInspection/NamespaceProvider/NamespaceFoldersToSkip";

    /// <summary>
    /// What ReSharper writes when it creates a layer from nothing. The namespaces are load-bearing:
    /// the file is XAML, and <c>s:Boolean</c> is only a type because <c>s</c> resolves.
    /// </summary>
    private const string EmptyLayer =
        """
        <wpf:ResourceDictionary xml:space="preserve" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:s="clr-namespace:System;assembly=mscorlib" xmlns:ss="urn:shemas-jetbrains-com:settings-storage-xaml" xmlns:wpf="http://schemas.microsoft.com/winfx/2006/xaml/presentation"></wpf:ResourceDictionary>
        """;

    /// <summary>
    /// Marks a folder as contributing a namespace segment, or as not contributing one.
    /// </summary>
    /// <param name="projectPath">The project that owns the folder; its layer is written.</param>
    /// <param name="folderPath">The folder, absolute or relative to the project.</param>
    /// <param name="isProvider">
    /// True to have the folder name appear in namespaces, which is the default and is spelled by
    /// the absence of an entry. False writes the entry that takes it out.
    /// </param>
    public static bool SetNamespaceProvider(string projectPath, string folderPath, bool isProvider)
    {
        string projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        string relative = Path.GetRelativePath(projectDirectory, Path.GetFullPath(folderPath))
            .Replace('/', '\\')
            .Trim('\\');

        if (relative.Length == 0 || relative.StartsWith("..", StringComparison.Ordinal))
            return false;

        // Lower-cased before escaping, which is how ReSharper writes it. The reader compares
        // case-insensitively either way; writing it differently would leave two entries for one
        // folder, and only one of them would be the one ReSharper edits next.
        string key = $"{NamespaceFoldersKey}/={DotSettingsEscaping.Encode(relative.ToLowerInvariant())}"
            + "/@EntryIndexedValue";

        string layer = projectPath + ".DotSettings";

        try
        {
            var document = Parser.ParseText(
                File.Exists(layer) ? File.ReadAllText(layer) : EmptyLayer);

            if (document.RootSyntax is not { } original)
                return false;

            // By local name, because the prefix bound to the XAML namespace is the file's choice
            // and only ReSharper's own files are guaranteed to call it x.
            var existing = original.Descendants().FirstOrDefault(element => string.Equals(
                element.GetAttributeValueByLocalName("Key"), key, StringComparison.Ordinal));

            var root = original;

            if (isProvider)
            {
                // The default is the absence of an entry, so removing ours says it. When a weaker
                // layer still says otherwise, saying nothing would not be enough — a False is
                // what overrides it, and it is what ReSharper itself writes in that case.
                if (existing is not null)
                    root = root.RemoveNode(existing, SyntaxRemoveOptions.KeepNoTrivia)!;

                if (SkippedByAnotherLayer(projectPath, layer, relative))
                    root = AddEntry(root, key, "False");
            }
            else if (existing is not null)
            {
                root = root.ReplaceNode(existing, existing.WithText("True"));
            }
            else
            {
                root = AddEntry(root, key, "True");
            }

            if (ReferenceEquals(root, original))
                return true;

            File.WriteAllText(layer, document.ReplaceNode(original, root).ToFullString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        ReSharperSettings.Clear();
        return true;
    }

    private static XmlElementBaseSyntax AddEntry(
        XmlElementBaseSyntax root, string key, string value)
    {
        var added = root.AddElement("s:Boolean", out var entry, (_, e) => e.SetAttribute("x:Key", key));
        return added.ReplaceNode(entry, entry.WithText(value));
    }

    /// <summary>
    /// Whether a layer other than the one being written still takes this folder out.
    /// </summary>
    /// <remarks>
    /// Only the layers below matter for that question, but the personal layer above ours can say
    /// it too, and a False we write would not reach it — so it is included, and the answer is
    /// "somebody else said skip". Being wrong here costs a redundant <c>False</c> entry, which is
    /// what ReSharper leaves behind as well.
    /// </remarks>
    private static bool SkippedByAnotherLayer(string projectPath, string ours, string relative)
    {
        foreach (string layer in DotSettingsLayers.For(projectPath))
        {
            if (string.Equals(layer, ours, StringComparison.OrdinalIgnoreCase))
                continue;

            if (DotSettingsDocumentCache.Get(layer) is not { } document)
                continue;

            foreach (var entry in document.Entries)
            {
                // Indices arrive decoded, and a folder is stored the way a path is written:
                // separators either way round, no leading or trailing one.
                if (entry.Path == "CodeInspection/NamespaceProvider/NamespaceFoldersToSkip"
                    && entry.IsPresentIndex
                    && string.Equals(
                        entry.Index!.Replace('/', '\\').Trim('\\'),
                        relative,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

using RoslynMCP.Lsp.Handlers;

namespace RoslynMCP.Languages;

/// <summary>
/// Which pack owns a document, decided from its path.
/// </summary>
/// <remarks>
/// Shared by <see cref="LanguageRegistry"/> and <see cref="LanguageSession"/>, which ask the same
/// question of different sets — every registered pack, and the ones one editor connection switched
/// on. They used to carry a copy of this each, which is one copy too many for a rule that decides
/// whether a request reaches a pack at all.
/// </remarks>
internal sealed class LanguageFileMap
{
    private readonly Dictionary<string, ILanguagePack> _byExtension =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, ILanguagePack> _byFileName =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<ILanguagePack> _byNamePattern = [];

    public LanguageFileMap(IEnumerable<ILanguagePack> packs)
    {
        foreach (var pack in packs)
        {
            foreach (string extension in pack.FileExtensions)
                _byExtension.TryAdd(extension, pack);

            foreach (string fileName in pack.FileNames)
                _byFileName.TryAdd(fileName, pack);

            // Every pack is offered every unmatched name; the default answers no, so only packs
            // overriding OwnsFileName pay anything and registration order breaks ties.
            _byNamePattern.Add(pack);
        }
    }

    /// <summary>The pack owning this document, or null when C# should answer.</summary>
    /// <remarks>
    /// Name before extension, because the two disagree on purpose. A pack claiming
    /// <c>packages.config</c> wants that one file and not <c>web.config</c> beside it, which belongs
    /// to the binding-redirect handler; claiming <c>.config</c> outright would take both.
    /// </remarks>
    public ILanguagePack? Resolve(string? uriOrPath)
    {
        if (uriOrPath is not { Length: > 0 } candidate || IsVirtual(candidate))
            return null;

        string fileName = Path.GetFileName(candidate);
        if (fileName.Length > 0)
        {
            if (_byFileName.TryGetValue(fileName, out var byName))
                return byName;

            foreach (var patterned in _byNamePattern)
            {
                if (patterned.OwnsFileName(fileName))
                    return patterned;
            }
        }

        string extension = Path.GetExtension(candidate);
        return extension.Length > 0 && _byExtension.TryGetValue(extension, out var pack) ? pack : null;
    }

    /// <summary>
    /// Documents served from memory rather than from disk — generated sources and decompiled
    /// metadata. Their URIs are not file paths and their extension says C#, so no pack may claim
    /// one; the exclusion lives here so every pack inherits it.
    /// </summary>
    private static bool IsVirtual(string uriOrPath) =>
        uriOrPath.StartsWith(VirtualDocumentHandler.GeneratedScheme + ":", StringComparison.Ordinal)
        || uriOrPath.StartsWith(VirtualDocumentHandler.MetadataScheme + ":", StringComparison.Ordinal);
}

using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.Resources.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Languages.Resources;

/// <summary>
/// Find-references on a resource key: its declaration in every file of the family, and every call
/// site that reads it in C# or in markup.
/// </summary>
/// <remarks>
/// Answerable where <see cref="ISymbolFreeRenameProvider"/> refuses. A call site whose root only
/// resolved by proximity is still reported, because a reference list with one extra entry is a
/// nuisance where a rename across a guessed file set is corruption.
/// </remarks>
internal sealed partial class ResourcesLanguage : ISymbolFreeReferenceProvider, ILanguageReferencesProvider
{
    public async Task<IReadOnlyList<LspLocation>?> ReferencesAsync(
        string filePath, int offset, Project? project, CancellationToken ct)
    {
        project ??= await ProjectOfAsync(filePath, ct);

        if (await ResourceKeySearch.LocateAsync(Settings, filePath, offset, project, ct)
            is not { } target)
        {
            return null;
        }

        var (sites, _) = await ResourceKeySearch.CollectAsync(Settings, target, ct);
        var results = new List<LspLocation>(sites.Length);

        foreach (var site in sites)
        {
            results.Add(new LspLocation(
                LspConverters.PathToUri(site.FilePath),
                LspConverters.ToRange(site.Text.Lines, site.Span)));
        }

        return results;
    }

    public async Task<LspLocation[]> ReferencesAsync(ReferenceParams p, CancellationToken ct) =>
        Caret(p.TextDocument, p.Position) is { } at
            && await ReferencesAsync(at.Path, at.Offset, project: null, ct) is { } found
                ? [.. found]
                : [];
}

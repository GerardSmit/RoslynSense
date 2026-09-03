using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.Resources.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Languages.Resources;

/// <summary>
/// Find-references on a resource key: every call site that reads it in C# or in markup, and the
/// original declaration — not the translations of it.
/// </summary>
/// <remarks>
/// Answerable where <see cref="ISymbolFreeRenameProvider"/> refuses. A call site whose root only
/// resolved by proximity is still reported, because a reference list with one extra entry is a
/// nuisance where a rename across a guessed file set is corruption.
/// </remarks>
internal sealed partial class ResourcesLanguage : ISymbolFreeReferenceProvider, ILanguageReferencesProvider
{
    public Task<IReadOnlyList<LspLocation>?> ReferencesAsync(
        string filePath, int offset, Project? project, CancellationToken ct) =>
        ReferencesAsync(filePath, offset, project, includeDeclaration: true, ct);

    private async Task<IReadOnlyList<LspLocation>?> ReferencesAsync(
        string filePath, int offset, Project? project, bool includeDeclaration, CancellationToken ct)
    {
        project ??= await ProjectOfAsync(filePath, ct);

        if (await ResourceKeySearch.LocateAsync(Settings, filePath, offset, project, ct)
            is not { } target)
        {
            return null;
        }

        // The neutral entry at most, never the translations. A key is declared once per culture and
        // once per portal override, so a family answering for a shipped product listed a dozen
        // spellings of the same string ahead of the two or three places that actually read it.
        var (sites, _) = await ResourceKeySearch.CollectAsync(
            Settings,
            target,
            ct,
            includeDeclaration
                ? ResourceKeySearch.DeclarationScope.NeutralOnly
                : ResourceKeySearch.DeclarationScope.None);
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
            && await ReferencesAsync(
                at.Path, at.Offset, project: null, p.Context.IncludeDeclaration, ct) is { } found
                ? [.. found]
                : [];
}

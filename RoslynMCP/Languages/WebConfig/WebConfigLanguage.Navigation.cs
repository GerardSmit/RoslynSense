using RoslynMCP.Languages.WebConfig.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.WebConfig;

internal sealed partial class WebConfigLanguage :
    ILanguageDefinitionProvider, ILanguageReferencesProvider
{
    public async Task<Location[]> ReferencesAsync(ReferenceParams p, CancellationToken ct)
    {
        if (await ViewAndEntryAsync(p.TextDocument.Uri, p.Position, ct) is not ({ } view, { } entry))
            return [];

        return WebConfigReferenceService.Usages(view, entry);
    }

    /// <summary>
    /// Definition from an entry goes to the other declarations of the same name — the config in a
    /// subdirectory that overrides it, or the application-level one it overrides.
    /// </summary>
    /// <remarks>
    /// There is no property to land on the way there is for a bound appsettings key: a Framework
    /// setting is a string in a collection. What F12 can still answer is the question the override
    /// chain makes real — "where else is this decided" — and answering nothing when the name is
    /// declared once lets the editor say so rather than navigating to the caret's own line.
    /// </remarks>
    public async Task<Location[]> DefinitionAsync(
        TextDocumentPositionParams p, bool typeDefinition, CancellationToken ct)
    {
        if (await ViewAndEntryAsync(p.TextDocument.Uri, p.Position, ct) is not ({ } view, { } entry))
            return [];

        string here = LspConverters.PathToUri(view.FilePath);

        return
        [
            .. WebConfigReferenceService
                .Declarations(view.Project?.FilePath, entry.Section, entry.Name)
                .Where(location => !string.Equals(location.Uri, here, StringComparison.OrdinalIgnoreCase)),
        ];
    }

    private static async Task<(WebConfigView? View, WebConfigEntry? Entry)> ViewAndEntryAsync(
        string uri, Position position, CancellationToken ct)
    {
        if (await WebConfigWorkspace.GetAsync(LspConverters.UriToPath(uri), ct) is not { } view)
            return (null, null);

        int offset = LspConverters.ToOffset(view.Text, position);
        return (view, view.Document.EntryAt(offset));
    }
}

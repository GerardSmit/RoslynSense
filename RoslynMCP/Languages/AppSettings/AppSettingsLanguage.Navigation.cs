using RoslynMCP.Languages.AppSettings.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.AppSettings;

internal sealed partial class AppSettingsLanguage :
    ILanguageDefinitionProvider, ILanguageReferencesProvider
{
    /// <summary>
    /// Definition from a key goes to the property it binds to — the one place the key's name is
    /// a symbol. A key without a binding has no better definition than itself, and answering
    /// nothing lets the editor say so.
    /// </summary>
    public async Task<Location[]> DefinitionAsync(
        TextDocumentPositionParams p, bool typeDefinition, CancellationToken ct)
    {
        if (await ViewAndKeyAsync(p.TextDocument.Uri, p.Position, ct) is not ({ } view, { } key))
            return [];

        return AppSettingsReferenceService.BoundPropertyLocation(view, key) is { } location
            ? [location]
            : [];
    }

    public async Task<Location[]> ReferencesAsync(ReferenceParams p, CancellationToken ct)
    {
        if (await ViewAndKeyAsync(p.TextDocument.Uri, p.Position, ct) is not ({ } view, { } key))
            return [];

        return await AppSettingsReferenceService.UsagesAsync(view, key, ct);
    }

    private static async Task<(AppSettingsView? View, AppSettingsKey? Key)> ViewAndKeyAsync(
        string uri, Position position, CancellationToken ct)
    {
        if (await AppSettingsWorkspace.GetAsync(LspConverters.UriToPath(uri), ct) is not { } view)
            return (null, null);

        int offset = LspConverters.ToOffset(view.Text, position);
        return (view, view.Document.KeyAt(offset));
    }
}

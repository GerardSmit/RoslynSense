using RoslynMCP.Languages.WebForms.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.WebForms;

internal sealed partial class WebFormsLanguage :
    ILanguageDefinitionProvider,
    ILanguageImplementationProvider,
    ILanguageReferencesProvider,
    ILanguageHoverProvider,
    ILanguageDocumentHighlightProvider
{
    public Task<Location[]> DefinitionAsync(
        TextDocumentPositionParams p, bool typeDefinition, CancellationToken ct) =>
        AspxLanguageHandler.DefinitionAsync(p, typeDefinition, ct);

    public Task<Location[]> ImplementationAsync(TextDocumentPositionParams p, CancellationToken ct) =>
        AspxLanguageHandler.ImplementationAsync(p, ct);

    public Task<Location[]> ReferencesAsync(ReferenceParams p, CancellationToken ct) =>
        AspxLanguageHandler.ReferencesAsync(p, ct);

    public Task<Hover?> HoverAsync(TextDocumentPositionParams p, CancellationToken ct) =>
        AspxLanguageHandler.HoverAsync(p, ct);

    public Task<DocumentHighlight[]> DocumentHighlightAsync(
        TextDocumentPositionParams p, CancellationToken ct) =>
        AspxLanguageHandler.DocumentHighlightAsync(p, ct);
}

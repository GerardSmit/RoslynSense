using RoslynMCP.Languages.Proto.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.Proto;

internal sealed partial class ProtoLanguage :
    ILanguageDefinitionProvider,
    ILanguageImplementationProvider,
    ILanguageReferencesProvider,
    ILanguageHoverProvider,
    ILanguageDocumentHighlightProvider
{
    public Task<Location[]> DefinitionAsync(
        TextDocumentPositionParams p, bool typeDefinition, CancellationToken ct) =>
        ProtoNavigationHandler.DefinitionAsync(p, typeDefinition, ct);

    public Task<Location[]> ImplementationAsync(TextDocumentPositionParams p, CancellationToken ct) =>
        ProtoNavigationHandler.ImplementationAsync(p, ct);

    public Task<Location[]> ReferencesAsync(ReferenceParams p, CancellationToken ct) =>
        ProtoNavigationHandler.ReferencesAsync(p, ct);

    public Task<Hover?> HoverAsync(TextDocumentPositionParams p, CancellationToken ct) =>
        ProtoNavigationHandler.HoverAsync(p, ct);

    public Task<DocumentHighlight[]> DocumentHighlightAsync(
        TextDocumentPositionParams p, CancellationToken ct) =>
        ProtoNavigationHandler.DocumentHighlightAsync(p, ct);
}

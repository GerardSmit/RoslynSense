using RoslynMCP.Languages.WebForms.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.WebForms;

internal sealed partial class WebFormsLanguage :
    ILanguageDocumentSymbolProvider,
    ILanguageFoldingRangeProvider
{
    public Task<DocumentSymbol[]> DocumentSymbolAsync(DocumentSymbolParams p, CancellationToken ct) =>
        AspxLanguageHandler.DocumentSymbolAsync(p, ct);

    public Task<FoldingRange[]> FoldingRangeAsync(FoldingRangeParams p, CancellationToken ct) =>
        AspxLanguageHandler.FoldingRangeAsync(p, ct);
}

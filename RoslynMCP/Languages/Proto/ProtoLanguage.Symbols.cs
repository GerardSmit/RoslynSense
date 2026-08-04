using RoslynMCP.Languages.Proto.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.Proto;

internal sealed partial class ProtoLanguage :
    ILanguageDocumentSymbolProvider,
    ILanguageFoldingRangeProvider
{
    public Task<DocumentSymbol[]> DocumentSymbolAsync(DocumentSymbolParams p, CancellationToken ct) =>
        ProtoNavigationHandler.DocumentSymbolAsync(p, ct);

    public Task<FoldingRange[]> FoldingRangeAsync(FoldingRangeParams p, CancellationToken ct) =>
        ProtoNavigationHandler.FoldingRangeAsync(p, ct);
}

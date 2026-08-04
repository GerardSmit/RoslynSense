using RoslynMCP.Languages.WebForms.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.WebForms;

internal sealed partial class WebFormsLanguage :
    ILanguageRenameProvider,
    ILanguageSignatureHelpProvider,
    ILanguageDiagnosticProvider
{
    public Task<PrepareRenameResult?> PrepareRenameAsync(
        TextDocumentPositionParams p, CancellationToken ct) =>
        AspxLanguageHandler.PrepareRenameAsync(p, ct);

    public Task<WorkspaceEdit?> RenameAsync(RenameParams p, CancellationToken ct) =>
        AspxLanguageHandler.RenameAsync(p, ct);

    public Task<SignatureHelp?> SignatureHelpAsync(SignatureHelpParams p, CancellationToken ct) =>
        AspxLanguageHandler.SignatureHelpAsync(p, ct);

    public Task<Diagnostic[]> DiagnosticsAsync(string filePath, CancellationToken ct) =>
        AspxLanguageHandler.DiagnosticsAsync(filePath, ct);
}

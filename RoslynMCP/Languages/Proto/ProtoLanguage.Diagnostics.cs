using RoslynMCP.Languages.Proto.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.Proto;

internal sealed partial class ProtoLanguage : ILanguageDiagnosticProvider
{
    public Task<Diagnostic[]> DiagnosticsAsync(string filePath, CancellationToken ct) =>
        ProtoDiagnosticsHandler.DiagnosticsAsync(filePath, ct);
}

using RoslynMCP.Languages.Proto.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.Proto;

// Deliberately a document provider only: C#-initiated rename never rewrites a schema.
internal sealed partial class ProtoLanguage : ILanguageRenameProvider
{
    public Task<PrepareRenameResult?> PrepareRenameAsync(TextDocumentPositionParams p, CancellationToken ct) =>
        ProtoRenameHandler.PrepareRenameAsync(p, ct);

    public Task<WorkspaceEdit?> RenameAsync(RenameParams p, CancellationToken ct) =>
        ProtoRenameHandler.RenameAsync(p, ct);
}

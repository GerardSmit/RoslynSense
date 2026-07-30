using Microsoft.CodeAnalysis;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>Semantic diagnostics for one document — shared by push
/// (<see cref="DiagnosticsPublisher"/>) and pull (textDocument/diagnostic).</summary>
internal static class DiagnosticsHandler
{
    public static async Task<Protocol.Diagnostic[]> ComputeAsync(string filePath, CancellationToken ct)
    {
        var document = await LspDocumentResolver.ResolveAsync(filePath, ct);
        var model = document is null ? null : await document.GetSemanticModelAsync(ct);
        if (model is null)
            return Array.Empty<Protocol.Diagnostic>();

        return model.GetDiagnostics(cancellationToken: ct)
            .Where(d => d.Severity != DiagnosticSeverity.Hidden && d.Location.IsInSource)
            .Select(d => new Protocol.Diagnostic(
                LspConverters.ToRange(d.Location.GetLineSpan().Span),
                LspConverters.ToLspSeverity(d.Severity),
                d.Id,
                "roslyn-sense",
                d.GetMessage()))
            .ToArray();
    }

    /// <summary>Pull with resultId versioning: the id encodes the document text checksum and
    /// the project's dependent-semantic version, so an unchanged world answers "unchanged"
    /// without recomputing diagnostics.</summary>
    public static async Task<object> PullAsync(
        DocumentDiagnosticParams p, CancellationToken ct)
    {
        string path = LspConverters.UriToPath(p.TextDocument.Uri);

        string? resultId = null;
        var document = await LspDocumentResolver.ResolveAsync(path, ct);
        if (document is not null)
        {
            var text = await document.GetTextAsync(ct);
            var semanticVersion = await document.Project.GetDependentSemanticVersionAsync(ct);
            resultId = $"{Convert.ToHexString(text.GetChecksum().AsSpan())}:{semanticVersion}";
            if (p.PreviousResultId is not null && p.PreviousResultId == resultId)
                return new UnchangedDocumentDiagnosticReport("unchanged", resultId);
        }

        var items = await ComputeAsync(path, ct);
        return new FullDocumentDiagnosticReport("full", items) { ResultId = resultId };
    }
}

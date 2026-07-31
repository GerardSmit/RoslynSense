using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMCP.Config;
using RoslynMCP.Lsp.Protocol;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>Diagnostics for one document — shared by push (<see cref="DiagnosticsPublisher"/>)
/// and pull (textDocument/diagnostic). Compiler diagnostics are cheap and always computed;
/// analyzer diagnostics ride the <see cref="AnalyzerDiagnosticCache"/> so they never block a
/// keystroke or a pull.</summary>
internal static class DiagnosticsHandler
{
    /// <summary>Compiler diagnostics only — the fast pass.</summary>
    public static async Task<Protocol.Diagnostic[]> ComputeAsync(string filePath, CancellationToken ct)
    {
        var document = await LspDocumentResolver.ResolveAsync(filePath, ct);
        if (document is null)
            return Array.Empty<Protocol.Diagnostic>();

        return ToProtocol(await CompilerDiagnosticsAsync(document, ct));
    }

    /// <summary>Compiler plus analyzer diagnostics, computing the analyzer pass if it is not
    /// already cached. The slow pass.</summary>
    public static async Task<Protocol.Diagnostic[]> ComputeWithAnalyzersAsync(
        string filePath, CancellationToken ct)
    {
        var document = await LspDocumentResolver.ResolveAsync(filePath, ct);
        if (document is null)
            return Array.Empty<Protocol.Diagnostic>();

        var compiler = await CompilerDiagnosticsAsync(document, ct);
        var analyzer = await AnalyzerDiagnosticCache.GetOrComputeAsync(document, ct);
        return ToProtocol(Merge(compiler, analyzer));
    }

    private static async Task<ImmutableArray<RoslynDiagnostic>> CompilerDiagnosticsAsync(
        Document document, CancellationToken ct)
    {
        var model = await document.GetSemanticModelAsync(ct);
        return model is null
            ? ImmutableArray<RoslynDiagnostic>.Empty
            : model.GetDiagnostics(cancellationToken: ct);
    }

    /// <summary>Union of both sources, deduplicated on id + span: an analyzer reporting what the
    /// compiler already reported must not draw two squiggles.</summary>
    internal static IEnumerable<RoslynDiagnostic> Merge(
        IEnumerable<RoslynDiagnostic> compiler, IEnumerable<RoslynDiagnostic> analyzer) =>
        compiler.Concat(analyzer)
            .GroupBy(d => (d.Id, d.Location.SourceSpan))
            .Select(g => g.First());

    private static Protocol.Diagnostic[] ToProtocol(IEnumerable<RoslynDiagnostic> diagnostics) =>
        diagnostics
            .Where(d => d.Severity != DiagnosticSeverity.Hidden && d.Location.IsInSource)
            .Select(d => new Protocol.Diagnostic(
                LspConverters.ToRange(d.Location.GetLineSpan().Span),
                LspConverters.ToLspSeverity(d.Severity),
                d.Id,
                "roslyn-sense",
                d.GetMessage()))
            .ToArray();

    /// <summary>Pull with resultId versioning: the id encodes the document text checksum and
    /// the project's dependent-semantic version, so an unchanged world answers "unchanged"
    /// without recomputing diagnostics.
    /// Analyzer diagnostics are served from cache only — a miss returns compiler diagnostics
    /// immediately and computes in the background, then asks the client to re-pull. Blocking a
    /// pull on analyzers would make every first request feel like a hang.</summary>
    public static async Task<object> PullAsync(
        DocumentDiagnosticParams p, CancellationToken ct)
    {
        string path = LspConverters.UriToPath(p.TextDocument.Uri);

        var document = await LspDocumentResolver.ResolveAsync(path, ct);
        if (document is null)
            return new FullDocumentDiagnosticReport("full", Array.Empty<Protocol.Diagnostic>());

        string? version = await AnalyzerDiagnosticCache.GetVersionAsync(document, ct);
        var analyzer = AnalyzerDiagnosticCache.TryGet(document, version);
        bool analyzersPending = LspFeatureOptions.AnalyzerDiagnostics &&
            !AnalyzerDiagnosticCache.IsComputed(document, version);

        // The resultId distinguishes "compiler only" from "compiler + analyzers" for the same
        // text; without that, the follow-up pull after the background pass answers "unchanged"
        // and the analyzer squiggles never appear.
        string? resultId = version is null ? null : $"{version}:{(analyzersPending ? "c" : "a")}";
        if (resultId is not null && p.PreviousResultId == resultId)
            return new UnchangedDocumentDiagnosticReport("unchanged", resultId);

        var compiler = await CompilerDiagnosticsAsync(document, ct);

        if (analyzersPending)
            ComputeAnalyzersInBackground(document);

        return new FullDocumentDiagnosticReport("full", ToProtocol(Merge(compiler, analyzer)))
        {
            ResultId = resultId,
        };
    }

    private static void ComputeAnalyzersInBackground(Document document) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await AnalyzerDiagnosticCache.GetOrComputeAsync(document, CancellationToken.None);
                await LspSessionRegistry.RequestRefreshAsync(RefreshKind.Diagnostics);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Lsp] Background analyzers for '{document.Name}' failed: {ex.Message}");
            }
        });
}

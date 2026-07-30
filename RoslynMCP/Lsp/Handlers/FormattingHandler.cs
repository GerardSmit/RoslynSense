using Microsoft.CodeAnalysis.Formatting;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Lsp.Handlers;

internal static class FormattingHandler
{
    public static Task<TextEdit[]> FormatAsync(DocumentFormattingParams p, CancellationToken ct) =>
        FormatCoreAsync(p.TextDocument, range: null, ct);

    public static Task<TextEdit[]> FormatRangeAsync(DocumentRangeFormattingParams p, CancellationToken ct) =>
        FormatCoreAsync(p.TextDocument, p.Range, ct);

    private static async Task<TextEdit[]> FormatCoreAsync(
        TextDocumentIdentifier textDocument, Protocol.Range? range, CancellationToken ct)
    {
        var document = await LspDocumentResolver.ResolveAsync(
            LspConverters.UriToPath(textDocument.Uri), ct);
        if (document is null)
            return Array.Empty<TextEdit>();

        var oldText = await document.GetTextAsync(ct);
        var formatted = range is null
            ? await Formatter.FormatAsync(document, cancellationToken: ct)
            : await Formatter.FormatAsync(document, LspConverters.ToTextSpan(oldText, range), cancellationToken: ct);
        var changes = await formatted.GetTextChangesAsync(document, ct);

        return changes
            .Select(c => new TextEdit(LspConverters.ToRange(oldText.Lines, c.Span), c.NewText ?? ""))
            .ToArray();
    }
}

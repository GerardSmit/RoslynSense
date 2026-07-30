using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Lsp.Handlers;

internal static class FormattingHandler
{
    public static Task<TextEdit[]> FormatAsync(DocumentFormattingParams p, CancellationToken ct) =>
        FormatCoreAsync(p.TextDocument, range: null, ct);

    public static Task<TextEdit[]> FormatRangeAsync(DocumentRangeFormattingParams p, CancellationToken ct) =>
        FormatCoreAsync(p.TextDocument, p.Range, ct);

    /// <summary>textDocument/onTypeFormatting: after ";" or "}" formats the enclosing
    /// statement/member; after newline re-indents the previous and current line. This is
    /// what gives fresh lines the correct indentation as you type.</summary>
    public static async Task<TextEdit[]> FormatOnTypeAsync(
        DocumentOnTypeFormattingParams p, CancellationToken ct)
    {
        var resolved = await HandlerHelpers.ResolveAsync(p.TextDocument, p.Position, ct);
        if (resolved is not var (document, text, offset))
            return Array.Empty<TextEdit>();

        TextSpan span;
        if (p.Character == "\n")
        {
            int startLine = Math.Max(0, p.Position.Line - 1);
            span = TextSpan.FromBounds(
                text.Lines[startLine].Start,
                text.Lines[Math.Min(p.Position.Line, text.Lines.Count - 1)].End);
        }
        else
        {
            var root = await document.GetSyntaxRootAsync(ct);
            var node = root?.FindToken(Math.Max(0, offset - 1)).Parent?
                .AncestorsAndSelf()
                .FirstOrDefault(n => n is StatementSyntax or MemberDeclarationSyntax);
            if (node is null)
                return Array.Empty<TextEdit>();
            span = node.Span;
        }

        var formatted = await Formatter.FormatAsync(document, span, cancellationToken: ct);
        var changes = await formatted.GetTextChangesAsync(document, ct);
        return changes
            .Select(c => new TextEdit(LspConverters.ToRange(text.Lines, c.Span), c.NewText ?? ""))
            .ToArray();
    }

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

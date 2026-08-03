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

    /// <summary>
    /// textDocument/onTypeFormatting: after ";" or "}", formats the enclosing statement or
    /// member.
    /// </summary>
    /// <remarks>
    /// Newline is deliberately not a trigger. Roslyn's formatter indents lines that contain a
    /// token, and the line under the caret after Enter contains none — so formatting a span
    /// that reaches into it removes the indentation the editor had just inserted and the caret
    /// jumps to column zero. Indenting a line that has nothing on it yet is a different service
    /// from formatting one that does, which is why Roslyn's own IDE keeps them apart and why
    /// its language server does not register newline either. The editor's bracket-based
    /// auto-indent handles Enter correctly on its own.
    /// </remarks>
    public static async Task<TextEdit[]> FormatOnTypeAsync(
        DocumentOnTypeFormattingParams p, CancellationToken ct)
    {
        // Defensive: a client that triggers on newline anyway gets nothing rather than an edit
        // that unindents it.
        if (p.Character is not (";" or "}"))
            return Array.Empty<TextEdit>();

        var resolved = await HandlerHelpers.ResolveAsync(p.TextDocument, p.Position, ct);
        if (resolved is not var (document, text, offset))
            return Array.Empty<TextEdit>();

        var root = await document.GetSyntaxRootAsync(ct);
        var node = root?.FindToken(Math.Max(0, offset - 1)).Parent?
            .AncestorsAndSelf()
            .FirstOrDefault(n => n is StatementSyntax or MemberDeclarationSyntax);
        if (node is null)
            return Array.Empty<TextEdit>();

        var formatted = await Formatter.FormatAsync(document, node.Span, cancellationToken: ct);
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

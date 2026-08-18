using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
    /// textDocument/onTypeFormatting: after ";" the enclosing statement, after "{" or "}" the
    /// construct that brace belongs to — its header and its own braces, never what is between
    /// them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The brace triggers are what put an opening brace on its own line: the user types
    /// <c>if (x) {</c> and the formatter moves the brace down, because that is what the
    /// configured brace style says (Roslyn reads <c>csharp_new_line_before_open_brace</c> from
    /// .editorconfig, so a codebase that puts braces on the same line keeps them there).
    /// </para>
    /// <para>
    /// The formatted spans stop at the braces on purpose. Formatting the whole statement instead
    /// reflows every line nested inside it, so closing an <c>if</c> rewrote the <c>try</c> in its
    /// body — an edit the user never asked for, on code they were not typing on.
    /// </para>
    /// <para>
    /// Newline is deliberately not a trigger. Roslyn's formatter indents lines that contain a
    /// token, and the line under the caret after Enter contains none — so formatting a span that
    /// reaches into it removes the indentation the editor had just inserted and the caret jumps
    /// to column zero. Enter is the editor's job, and the extension gives C# the indentation
    /// rules it needs to do it (language-configuration/csharp.json).
    /// </para>
    /// </remarks>
    public static async Task<TextEdit[]> FormatOnTypeAsync(
        DocumentOnTypeFormattingParams p, CancellationToken ct)
    {
        // Defensive: a client that triggers on newline anyway gets nothing rather than an edit
        // that unindents it.
        if (p.Character is not (";" or "}" or "{"))
            return Array.Empty<TextEdit>();

        var resolved = await HandlerHelpers.ResolveAsync(p.TextDocument, p.Position, ct);
        if (resolved is not var (document, text, offset))
            return Array.Empty<TextEdit>();

        var root = await document.GetSyntaxRootAsync(ct);
        if (root is null)
            return Array.Empty<TextEdit>();

        // The caret sits just past the character that was typed.
        var typed = root.FindToken(Math.Max(0, offset - 1));
        var spans = p.Character is ";" ? StatementSpans(typed) : BraceSpans(typed);
        if (spans.Length == 0)
            return Array.Empty<TextEdit>();

        var formatted = await Formatter.FormatAsync(document, spans, options: null, cancellationToken: ct);
        var changes = await formatted.GetTextChangesAsync(document, ct);
        return changes
            .Select(c => new TextEdit(LspConverters.ToRange(text.Lines, c.Span), c.NewText ?? ""))
            .ToArray();
    }

    private static TextSpan[] StatementSpans(SyntaxToken typed)
    {
        var node = typed.Parent?
            .AncestorsAndSelf()
            .FirstOrDefault(n => n is StatementSyntax or MemberDeclarationSyntax);
        return node is null ? Array.Empty<TextSpan>() : [node.Span];
    }

    /// <summary>
    /// The header up to and including the opening brace, plus — when the closing brace is the
    /// one that was typed — the whitespace in front of it. Everything nested in between is left
    /// exactly as the user wrote it.
    /// </summary>
    private static TextSpan[] BraceSpans(SyntaxToken typed)
    {
        if (typed.Kind() is not (SyntaxKind.OpenBraceToken or SyntaxKind.CloseBraceToken)
            || Braces(typed.Parent) is not var (open, close)
            || open.IsMissing)
        {
            return Array.Empty<TextSpan>();
        }

        var construct = Construct(typed.Parent!);
        // The previous token has to be inside the span for the formatter to have a say about
        // what separates it from the brace — that separation is the line break being added. On
        // an initializer or a lambda the construct starts at the brace itself, so its own start
        // is not far enough back.
        var previous = open.GetPreviousToken();
        int start = previous.Kind() is SyntaxKind.None
            ? construct.SpanStart
            : Math.Min(construct.SpanStart, previous.SpanStart);

        var header = TextSpan.FromBounds(start, open.Span.End);
        return typed.Kind() is SyntaxKind.OpenBraceToken || close.IsMissing
            ? [header]
            : [header, TextSpan.FromBounds(close.FullSpan.Start, close.Span.End)];
    }

    private static (SyntaxToken Open, SyntaxToken Close)? Braces(SyntaxNode? node) => node switch
    {
        BlockSyntax b => (b.OpenBraceToken, b.CloseBraceToken),
        AccessorListSyntax a => (a.OpenBraceToken, a.CloseBraceToken),
        BaseTypeDeclarationSyntax t => (t.OpenBraceToken, t.CloseBraceToken),
        NamespaceDeclarationSyntax n => (n.OpenBraceToken, n.CloseBraceToken),
        SwitchStatementSyntax s => (s.OpenBraceToken, s.CloseBraceToken),
        InitializerExpressionSyntax i => (i.OpenBraceToken, i.CloseBraceToken),
        AnonymousObjectCreationExpressionSyntax a => (a.OpenBraceToken, a.CloseBraceToken),
        _ => null,
    };

    /// <summary>
    /// The thing the brace belongs to. A block is only ever the body of something else — the
    /// <c>if</c>, the <c>catch</c>, the method — and it is that something's line the brace has
    /// to be placed against.
    /// </summary>
    private static SyntaxNode Construct(SyntaxNode braceOwner) => braceOwner switch
    {
        BlockSyntax
        {
            Parent: StatementSyntax or MemberDeclarationSyntax or AccessorDeclarationSyntax
                or AnonymousFunctionExpressionSyntax or CatchClauseSyntax or FinallyClauseSyntax
                or ElseClauseSyntax or LocalFunctionStatementSyntax
        } b => b.Parent!,
        AccessorListSyntax { Parent: { } owner } => owner,
        _ => braceOwner,
    };

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

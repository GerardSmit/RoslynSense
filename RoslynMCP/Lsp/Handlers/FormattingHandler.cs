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
    /// them — and after Enter, the same thing again when the character before it was "{".
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
    /// Newline is a trigger for one reason only: to finish what "{" started. The editor cancels
    /// an on-type format the instant the next character arrives, and "{" followed straight away
    /// by Enter is the normal way to open a block — so the brace-moving edit is exactly the one
    /// most likely to be thrown away. Enter re-issues it.
    /// </para>
    /// <para>
    /// It re-issues nothing else, and <see cref="WithoutCaretLine"/> enforces that. Roslyn's
    /// formatter indents lines that contain a token, and the line under the caret after Enter
    /// contains none — so any span reaching into it comes back as "replace this indentation with
    /// nothing" and the caret jumps to column zero. That is the bug newline used to cause, and
    /// the guard is what makes it unable to come back. Indenting the fresh line stays the
    /// editor's job, off the indentation rules the extension gives C#
    /// (language-configuration/csharp.json), where it costs no round trip.
    /// </para>
    /// </remarks>
    public static async Task<TextEdit[]> FormatOnTypeAsync(
        DocumentOnTypeFormattingParams p, CancellationToken ct)
    {
        if (p.Character is not (";" or "}" or "{" or "\n" or "\r\n"))
            return Array.Empty<TextEdit>();

        var resolved = await HandlerHelpers.ResolveAsync(p.TextDocument, p.Position, ct);
        if (resolved is not var (document, text, offset))
            return Array.Empty<TextEdit>();

        var root = await document.GetSyntaxRootAsync(ct);
        if (root is null)
            return Array.Empty<TextEdit>();

        // The caret sits just past the character that was typed — except after Enter, where what
        // matters is the last token typed before it.
        var typed = p.Character is "\n" or "\r\n"
            ? root.FindToken(Math.Max(0, offset - 1)).GetPreviousToken()
            : root.FindToken(Math.Max(0, offset - 1));
        var spans = p.Character switch
        {
            ";" => StatementSpans(typed),
            "\n" or "\r\n" => typed.IsKind(SyntaxKind.OpenBraceToken)
                ? BraceSpans(typed)
                : Array.Empty<TextSpan>(),
            _ => BraceSpans(typed),
        };
        if (spans.Length == 0)
            return Array.Empty<TextEdit>();

        var formatted = await Formatter.FormatAsync(document, spans, options: null, cancellationToken: ct);
        var changes = await formatted.GetTextChangesAsync(document, ct);
        var edits = changes
            .Select(c => ToEdit(c, text))
            .ToArray();

        return p.Character is "\n" or "\r\n" ? WithoutCaretLine(edits, p.Position.Line) : edits;
    }

    /// <summary>
    /// Nothing, if any edit would touch the line the caret is on. All or none rather than a
    /// filter: a half-applied brace move is worse than none, and an edit that reaches the caret
    /// line means the span was wrong, not that one edit was.
    /// </summary>
    private static TextEdit[] WithoutCaretLine(TextEdit[] edits, int caretLine) =>
        edits.Any(e => e.Range.Start.Line <= caretLine && e.Range.End.Line >= caretLine)
            ? Array.Empty<TextEdit>()
            : edits;

    private static TextSpan[] StatementSpans(SyntaxToken typed)
    {
        var node = typed.Parent?
            .AncestorsAndSelf()
            .FirstOrDefault(n => n is StatementSyntax or MemberDeclarationSyntax);
        if (node is null)
            return Array.Empty<TextSpan>();

        // Out to the header a braceless body belongs to. The spacing rules that turn "if(x)" into
        // "if (x)" live on the header, and the first statement ancestor of the ";" is the body
        // itself — so a braceless "if" was the one shape that never got them, while "if(x) {" has
        // always had them from the "{" trigger.
        //
        // Climbing is bounded by the absence of a block: a body written with braces is a
        // BlockSyntax and stops this immediately, which is what keeps the span off the siblings
        // in an ordinary block. A braceless body is the single statement just typed, so widening
        // to its header reflows nothing the user did not just write.
        while (node is StatementSyntax embedded && HeaderOf(embedded) is { } header)
            node = header;

        return [node.Span];
    }

    /// <summary>
    /// The control-flow statement <paramref name="statement"/> is the braceless body of, or null
    /// when it is not one — a block, a statement in a block, or a body written with braces.
    /// </summary>
    /// <remarks>
    /// An <c>else</c> body deliberately does not climb. Its header is the whole if-statement, so
    /// reaching it would drag the then-branch — braces, body and all — into the span, which is
    /// the reflow this handler exists to avoid. Nothing is lost: <c>else</c> takes no parentheses,
    /// and in <c>else if (x)</c> the inner if is the header its own body climbs to.
    /// </remarks>
    private static StatementSyntax? HeaderOf(StatementSyntax statement) =>
        statement is BlockSyntax ? null : statement.Parent switch
        {
            IfStatementSyntax p when p.Statement == statement => p,
            ForStatementSyntax p when p.Statement == statement => p,
            CommonForEachStatementSyntax p when p.Statement == statement => p,
            WhileStatementSyntax p when p.Statement == statement => p,
            DoStatementSyntax p when p.Statement == statement => p,
            UsingStatementSyntax p when p.Statement == statement => p,
            LockStatementSyntax p when p.Statement == statement => p,
            FixedStatementSyntax p when p.Statement == statement => p,
            _ => null,
        };

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
            .Select(c => ToEdit(c, oldText))
            .ToArray();
    }

    private static TextEdit ToEdit(TextChange change, SourceText source)
    {
        string oldValue = source.ToString(change.Span);
        string newValue = PreserveNewLines(change.NewText ?? "", source);
        int prefix = 0;
        int shared = Math.Min(oldValue.Length, newValue.Length);
        while (prefix < shared && oldValue[prefix] == newValue[prefix])
            prefix++;

        int suffix = 0;
        while (suffix < shared - prefix
               && oldValue[^(suffix + 1)] == newValue[^(suffix + 1)])
        {
            suffix++;
        }

        var span = TextSpan.FromBounds(
            change.Span.Start + prefix,
            change.Span.End - suffix);
        string replacement = newValue.Substring(prefix, newValue.Length - prefix - suffix);
        return new TextEdit(LspConverters.ToRange(source.Lines, span), replacement);
    }

    private static string PreserveNewLines(string value, SourceText source)
    {
        var line = source.Lines.FirstOrDefault(candidate =>
            candidate.SpanIncludingLineBreak.Length > candidate.Span.Length);
        if (line.SpanIncludingLineBreak.Length == line.Span.Length)
            return value;

        string newLine = source.ToString(TextSpan.FromBounds(line.End, line.EndIncludingLineBreak));
        if (newLine == "\n")
            return value.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');

        return value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", newLine, StringComparison.Ordinal);
    }
}

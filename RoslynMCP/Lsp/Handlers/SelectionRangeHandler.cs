using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>
/// textDocument/selectionRange — expand and shrink selection, Rider's <c>Ctrl+W</c>.
/// </summary>
/// <remarks>
/// The chain is the syntax tree's own nesting: token, then every ancestor node up to the
/// compilation unit. Building it from the tree rather than from brackets is what makes the
/// steps land on meaningful units — an argument, then the argument list, then the invocation —
/// instead of on whatever punctuation happened to be nearby.
/// </remarks>
internal static class SelectionRangeHandler
{
    public static async Task<Protocol.SelectionRange[]> SelectionRangesAsync(
        SelectionRangeParams p, CancellationToken ct)
    {
        var document = await LspDocumentResolver.ResolveAsync(
            LspConverters.UriToPath(p.TextDocument.Uri), ct);
        if (document is null)
            return [];

        var root = await document.GetSyntaxRootAsync(ct);
        var text = await document.GetTextAsync(ct);
        if (root is null)
            return [];

        var ranges = new List<Protocol.SelectionRange>(p.Positions.Length);
        foreach (var position in p.Positions)
        {
            ct.ThrowIfCancellationRequested();
            ranges.Add(ChainAt(root, text, LspConverters.ToOffset(text, position)));
        }
        return [.. ranges];
    }

    private static Protocol.SelectionRange ChainAt(SyntaxNode root, SourceText text, int offset)
    {
        var token = root.FindToken(offset, findInsideTrivia: true);

        // Widest first, so each step can be built with the one above it as its parent.
        var spans = new List<TextSpan>();
        for (var node = token.Parent; node is not null; node = node.Parent)
        {
            if (spans.Count == 0 || spans[^1] != node.Span)
                spans.Add(node.Span);
        }
        spans.Reverse();

        // The token itself is the innermost step: selecting one identifier before its whole
        // expression is the first thing the keystroke should do.
        if (spans.Count == 0 || spans[^1] != token.Span)
            spans.Add(token.Span);

        Protocol.SelectionRange? current = null;
        foreach (var span in spans)
            current = new Protocol.SelectionRange(LspConverters.ToRange(text.Lines, span), current);

        // Non-null: the loop always runs, because the token span is added when nothing else was.
        return current!;
    }
}

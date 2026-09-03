using Microsoft.CodeAnalysis;

namespace RoslynMCP.Lsp;

/// <summary>
/// The token a caret is on, for the requests that answer a position rather than a selection.
/// </summary>
/// <remarks>
/// A caret is a gap between characters, so at <c>name|(</c> it belongs to two tokens at once and
/// <see cref="SyntaxNode.FindToken(int, bool)"/> answers with the one on the right — the paren.
/// Every caller here wants the other one: pressing F2 or F12 with the caret against the end of a
/// name is the same gesture as pressing it in the middle, and Roslyn's own symbol lookup already
/// reads it that way, so a handler that disagrees answers about a symbol the caret was never on.
/// </remarks>
internal static class CaretTokens
{
    /// <summary>
    /// The token at <paramref name="offset"/> that <paramref name="accept"/> wants — the one the
    /// caret is inside, or failing that the one it is against the end of. Null when neither is
    /// wanted, which is the answer that hands the request back to whoever asked.
    /// </summary>
    public static SyntaxToken? Touching(SyntaxNode root, int offset, Func<SyntaxToken, bool> accept)
    {
        if (offset < 0 || offset > root.FullSpan.End)
            return null;

        var here = root.FindToken(offset);
        if (here.Span.Contains(offset) && accept(here))
            return here;

        if (offset == 0)
            return null;

        // Only a token that ends exactly here: anything further left is a token the caret is past,
        // not one it is touching.
        var before = root.FindToken(offset - 1);
        return before.Span.End == offset && accept(before) ? before : null;
    }
}

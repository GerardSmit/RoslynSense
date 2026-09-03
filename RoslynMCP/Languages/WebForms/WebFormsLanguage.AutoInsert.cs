using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using WebFormsCore.Nodes;
using LspRange = RoslynMCP.Lsp.Protocol.Range;

namespace RoslynMCP.Languages.WebForms;

/// <summary>
/// roslynSense/onAutoInsert for markup: typing <c>&gt;</c> on an open tag writes the matching
/// close tag, and typing the second dash of <c>&lt;%--</c> finishes the comment.
/// </summary>
/// <remarks>
/// The request carries no record of which character was typed, so — as the C# handler does with
/// <c>///</c> — the trigger is read back out of the buffer immediately before the caret. Both
/// insertions are refused whenever the text they would produce is already there, because the
/// client applies the edit without asking and a second close tag is worse than none.
/// </remarks>
internal sealed partial class WebFormsLanguage : ILanguageAutoInsertProvider
{
    /// <summary>HTML elements that hold no content, so there is no close tag to write. The
    /// parser still gives them an open tag with no <c>EndTag</c>, which is otherwise exactly the
    /// shape that asks for one.</summary>
    private static readonly HashSet<string> s_voidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input",
        "link", "meta", "param", "source", "track", "wbr",
    };

    private const string CommentClose = " --%>";

    public async Task<OnAutoInsertResult?> OnAutoInsertAsync(
        OnAutoInsertParams p, CancellationToken ct)
    {
        var document = await AspxDocumentService.GetAsync(
            LspConverters.UriToPath(p.TextDocument.Uri), ct);
        if (document is null)
            return null;

        var text = document.SourceText;
        int offset = LspConverters.ToOffset(text, p.Position);
        if (offset <= 0 || offset > text.Length)
            return null;

        return text[offset - 1] switch
        {
            '-' => CloseComment(text, p.Position, offset),
            '>' => document.Tree is { } root ? CloseTag(text, root, p.Position, offset) : null,
            _ => null,
        };
    }

    private static OnAutoInsertResult? CloseComment(SourceText text, Position position, int offset)
    {
        if (offset < 4 || text.ToString(TextSpan.FromBounds(offset - 4, offset)) != "<%--")
            return null;

        // Typing inside a comment that already has its closer — the user is widening an existing
        // one, not opening a new one.
        var line = text.Lines.GetLineFromPosition(offset);
        if (text.ToString(TextSpan.FromBounds(offset, line.End)).Contains("--%>", StringComparison.Ordinal))
            return null;

        // The caret lands on the space, which is where the comment text goes.
        return new OnAutoInsertResult(
            new TextEdit(new LspRange(position, position), CommentClose),
            new Position(position.Line, position.Character + 1));
    }

    private static OnAutoInsertResult? CloseTag(
        SourceText text, RootNode root, Position position, int offset)
    {
        // "/>" closed the element on the way past.
        if (offset >= 2 && text[offset - 2] == '/')
            return null;

        foreach (var element in AspxSymbolResolver.EnumerateElements(root))
        {
            if (element.StartTag.Range.End.Offset != offset)
                continue;

            // The parser already paired this tag with a close tag further down the file. Writing
            // a second one would orphan the first and silently reshape the page.
            if (element.EndTag is not null || s_voidElements.Contains(element.StartTag.Name.Value))
                return null;

            return new OnAutoInsertResult(
                new TextEdit(new LspRange(position, position), $"</{QualifiedName(element)}>"),
                position);
        }

        return null;
    }

    /// <summary>The name as the close tag has to spell it, prefix included.</summary>
    private static string QualifiedName(ElementNode element) =>
        element.StartTag.Namespace is { } prefix
            ? $"{prefix.Value}:{element.StartTag.Name.Value}"
            : element.StartTag.Name.Value;
}

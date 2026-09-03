using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using WebFormsCore.Models;
using WebFormsCore.Nodes;

namespace RoslynMCP.Languages.WebForms;

/// <summary>
/// textDocument/selectionRange for markup — expand and shrink selection over the parse tree
/// rather than over whatever punctuation happens to be nearby.
/// </summary>
/// <remarks>
/// The chain a caret in an attribute value walks is value, attribute, open tag, element, and
/// then one ancestor element per keypress up to the whole file. That is the same shape the C#
/// handler gets from Roslyn's ancestor walk; markup only has to supply the steps the parse tree
/// does not model as nodes — an attribute is a dictionary entry, and an element's extent is its
/// two tags rather than its <see cref="Node.Range"/>, which holds the tag name alone.
/// </remarks>
internal sealed partial class WebFormsLanguage : ILanguageSelectionRangeProvider
{
    public async Task<SelectionRange[]> SelectionRangesAsync(
        SelectionRangeParams p, CancellationToken ct)
    {
        var document = await AspxDocumentService.GetAsync(
            LspConverters.UriToPath(p.TextDocument.Uri), ct);
        if (document?.Tree is not { } root)
            return [];

        var chains = new List<SelectionRange>(p.Positions.Length);
        foreach (var position in p.Positions)
        {
            ct.ThrowIfCancellationRequested();
            chains.Add(ChainAt(document, root, LspConverters.ToOffset(document.SourceText, position)));
        }

        return [.. chains];
    }

    private static SelectionRange ChainAt(AspxDocument document, RootNode root, int offset)
    {
        var spans = new List<TextSpan> { new(0, document.SourceText.Length) };

        if (DirectiveAt(root, offset) is { } directive)
        {
            spans.Add(AspxSymbolResolver.Span(directive.Range));
            AddAttributeSpans(document, directive.Attributes, offset, spans);
        }
        else if (DeepestElementAt(root, offset) is { } element)
        {
            // Outermost first: each keypress leaves one more enclosing tag behind.
            var ancestors = new List<ElementNode>();
            for (var node = element; node is not null; node = node.Parent)
                ancestors.Add(node);
            ancestors.Reverse();

            foreach (var ancestor in ancestors)
                spans.Add(Extent(ancestor));

            if (AspxSymbolResolver.Contains(element.StartTag.Range, offset))
            {
                spans.Add(AspxSymbolResolver.Span(element.StartTag.Range));

                if (AspxSymbolResolver.Contains(element.StartTag.ElementRange, offset))
                    spans.Add(AspxSymbolResolver.Span(element.StartTag.ElementRange));
                else
                    AddAttributeSpans(document, element.RawAttributes, offset, spans);
            }
            else if (element.EndTag is { } endTag
                && AspxSymbolResolver.Contains(endTag.Range, offset))
            {
                spans.Add(AspxSymbolResolver.Span(endTag.Range));
                spans.Add(AspxSymbolResolver.Span(endTag.ElementRange));
            }
        }

        // Code sits between an element's tags rather than inside either of them, so it is the
        // step below whichever element contains it.
        if (CodeSpanAt(root, offset) is { } code)
            spans.Add(code);

        var chain = Nest(spans, offset);

        SelectionRange? current = null;
        foreach (var span in chain)
            current = new SelectionRange(
                LspConverters.ToRange(document.SourceText.Lines, span), current);

        // Non-null: the whole-document span always survives Nest.
        return current!;
    }

    /// <summary>
    /// Keeps only the spans that hold the caret and each strictly contain the one before them,
    /// which is what makes the chain safe to build from parts assembled in several passes.
    /// </summary>
    private static List<TextSpan> Nest(List<TextSpan> spans, int offset)
    {
        var result = new List<TextSpan>(spans.Count);

        foreach (var span in spans)
        {
            if (offset < span.Start || offset > span.End)
                continue;

            if (result.Count > 0 && (result[^1] == span || !result[^1].Contains(span)))
                continue;

            result.Add(span);
        }

        return result;
    }

    /// <summary>
    /// An element from its opening <c>&lt;</c> to its closing <c>&gt;</c>. Falls back to the
    /// start tag alone when the element is self-closing or its close tag never matched, which is
    /// the honest answer — the parser knows of no text past it that belongs to this element.
    /// </summary>
    private static TextSpan Extent(ElementNode element)
    {
        int start = element.StartTag.Range.Start.Offset;
        int end = (element.EndTag ?? element.StartTag).Range.End.Offset;
        return TextSpan.FromBounds(start, Math.Max(start, end));
    }

    private static ElementNode? DeepestElementAt(RootNode root, int offset)
    {
        ElementNode? best = null;
        var bestExtent = default(TextSpan);

        foreach (var element in AspxSymbolResolver.EnumerateElements(root))
        {
            var extent = Extent(element);
            if (offset < extent.Start || offset > extent.End)
                continue;

            if (best is null || extent.Length < bestExtent.Length)
                (best, bestExtent) = (element, extent);
        }

        return best;
    }

    private static DirectiveNode? DirectiveAt(RootNode root, int offset) =>
        root.Directives.FirstOrDefault(d => AspxSymbolResolver.Contains(d.Range, offset));

    private static void AddAttributeSpans(
        AspxDocument document,
        Dictionary<TokenString, AttributeValue> attributes,
        int offset,
        List<TextSpan> spans)
    {
        foreach (var (key, value) in attributes)
        {
            bool onKey = AspxSymbolResolver.Contains(key.Range, offset);
            bool onValue = HasValue(key, value) && AspxSymbolResolver.Contains(value.Range, offset);
            if (!onKey && !onValue)
                continue;

            spans.Add(AttributeSpan(document, key, value));
            spans.Add(AspxSymbolResolver.Span(onKey ? key.Range : value.Range));
            return;
        }
    }

    /// <summary>
    /// <c>Text="Hello"</c> including the quotes: the lexer's value range sits inside them, but
    /// the step after selecting the value is the whole attribute, not the value plus one quote.
    /// </summary>
    /// <remarks>
    /// Only a literal's range runs to the closing quote. A <c>&lt;%# %&gt;</c> or
    /// <c>&lt;%$ %&gt;</c> value is reported as the inner expression alone, so widening to its end
    /// would select <c>Text="&lt;%$ Resources: Title</c> — an attribute that stops halfway through
    /// the delimiter it opened. The key alone is the honest step there, and <c>Nest</c> drops it
    /// when the caret is on the value, leaving the element as the next step out.
    /// </remarks>
    private static TextSpan AttributeSpan(AspxDocument document, TokenString key, AttributeValue value)
    {
        int start = key.Range.Start.Offset;
        if (!HasValue(key, value) || value.Kind is not AttributeValueKind.Literal)
            return AspxSymbolResolver.Span(key.Range);

        int end = value.Range.End.Offset;
        if (IsQuote(document, value.Range.Start.Offset - 1) && IsQuote(document, end))
            end++;

        return TextSpan.FromBounds(start, Math.Max(start, end));
    }

    /// <summary>A bare attribute (<c>runat</c> with no <c>=</c>) leaves the value token default,
    /// whose range is all zeros and would otherwise read as a span at the start of the file.
    /// The test is the range rather than the text: <c>&lt;%$ Resources %&gt;</c> has an empty
    /// argument sitting at a real offset, and that is still an attribute with a value.</summary>
    private static bool HasValue(TokenString key, AttributeValue value) =>
        value.Range.End.Offset > key.Range.Start.Offset;

    private static bool IsQuote(AspxDocument document, int offset) =>
        offset >= 0 && offset < document.SourceText.Length
        && document.SourceText[offset] is '"' or '\'';

    private static TextSpan? CodeSpanAt(RootNode root, int offset)
    {
        foreach (var node in AllNodes(root))
        {
            var range = node switch
            {
                ExpressionNode expression => expression.Text.Range,
                StatementNode statement => statement.Text.Range,
                // The argument rather than the whole `<%$ … %>`: the key is what a selection is
                // useful on, and the step out from it is the element that holds the builder.
                ExpressionBuilderNode builder => builder.Argument.Range,
                _ => default,
            };

            if (range.End.Offset > range.Start.Offset && AspxSymbolResolver.Contains(range, offset))
                return AspxSymbolResolver.Span(range);
        }

        foreach (var script in root.ScriptBlocks)
        {
            if (AspxSymbolResolver.Contains(script.Range, offset))
                return AspxSymbolResolver.Span(script.Range);
        }

        return null;
    }

    /// <summary>Every node in the file, templates included — a template's contents hang off
    /// <see cref="RootNode.Templates"/> rather than off the child hierarchy.</summary>
    private static IEnumerable<Node> AllNodes(RootNode root)
    {
        foreach (var node in root.AllChildren)
            yield return node;

        foreach (var template in root.Templates)
        {
            foreach (var node in template.AllChildren)
                yield return node;
        }
    }
}

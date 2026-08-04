using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Languages.WebForms.Lsp;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.WebForms;

/// <summary>
/// textDocument/linkedEditingRange for markup: retyping <c>&lt;asp:Panel&gt;</c> retypes
/// <c>&lt;/asp:Panel&gt;</c> with it, so the pair never drifts apart mid-edit.
/// </summary>
/// <remarks>
/// The C# handler limits itself to symbols whose every reference is visible in the file, because
/// linked editing applies with no preview and no confirmation. A tag pair meets that condition by
/// construction: both ranges come out of one element of one parse tree, and there is nothing
/// else anywhere that has to move with them. An element the parser never matched a close tag to
/// is left alone — guessing where the missing tag would have been is exactly the blind edit the
/// C# rule exists to prevent.
/// </remarks>
internal sealed partial class WebFormsLanguage : ILanguageLinkedEditingProvider
{
    /// <summary>
    /// What the client keeps treating as the same tag name while the user types. The prefix and
    /// the colon are inside it because <c>asp:Panel</c> is one name: typing over the prefix has
    /// to keep the close tag in step just as typing over the local name does.
    /// </summary>
    private const string TagNamePattern = "[A-Za-z_][A-Za-z0-9_.:-]*";

    public async Task<LinkedEditingRanges?> LinkedEditingRangesAsync(
        TextDocumentPositionParams p, CancellationToken ct)
    {
        var document = await AspxDocumentService.GetAsync(
            LspConverters.UriToPath(p.TextDocument.Uri), ct);
        if (document?.Tree is not { } root)
            return null;

        int offset = LspConverters.ToOffset(document.SourceText, p.Position);

        foreach (var element in AspxSymbolResolver.EnumerateElements(root))
        {
            ct.ThrowIfCancellationRequested();

            if (element.EndTag is not { } endTag)
                continue;

            var open = element.StartTag.ElementRange;
            var close = endTag.ElementRange;

            if (!AspxSymbolResolver.Contains(open, offset)
                && !AspxSymbolResolver.Contains(close, offset))
            {
                continue;
            }

            return new LinkedEditingRanges(
                [
                    AspxLanguageHandler.ToRange(document, AspxSymbolResolver.Span(open)),
                    AspxLanguageHandler.ToRange(document, AspxSymbolResolver.Span(close)),
                ],
                TagNamePattern);
        }

        return null;
    }
}

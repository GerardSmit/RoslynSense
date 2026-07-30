using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;

namespace RoslynMCP.Lsp.Handlers;

internal static class HoverHandler
{
    private static readonly SymbolDisplayFormat s_displayFormat =
        SymbolDisplayFormat.CSharpErrorMessageFormat
            .WithMemberOptions(
                SymbolDisplayMemberOptions.IncludeParameters |
                SymbolDisplayMemberOptions.IncludeType |
                SymbolDisplayMemberOptions.IncludeRef |
                SymbolDisplayMemberOptions.IncludeContainingType |
                SymbolDisplayMemberOptions.IncludeModifiers)
            .WithParameterOptions(
                SymbolDisplayParameterOptions.IncludeName |
                SymbolDisplayParameterOptions.IncludeType |
                SymbolDisplayParameterOptions.IncludeParamsRefOut |
                SymbolDisplayParameterOptions.IncludeDefaultValue);

    public static async Task<Hover?> HoverAsync(TextDocumentPositionParams p, CancellationToken ct)
    {
        if (await HandlerHelpers.ResolveAsync(p.TextDocument, p.Position, ct) is not
            var (document, text, offset) || document is null)
            return null;

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, offset, ct);
        if (symbol is null)
            return null;

        var sb = new StringBuilder();
        sb.Append("```csharp\n");
        sb.Append(symbol.ToDisplayString(s_displayFormat));
        sb.Append("\n```");

        var xmlDoc = symbol.GetDocumentationCommentXml(cancellationToken: ct);
        if (!string.IsNullOrWhiteSpace(xmlDoc))
        {
            var summary = SymbolFormatter.ExtractXmlDocSection(xmlDoc, "summary");
            if (!string.IsNullOrWhiteSpace(summary))
                sb.Append("\n\n").Append(summary);

            var returns = SymbolFormatter.ExtractXmlDocSection(xmlDoc, "returns");
            if (!string.IsNullOrWhiteSpace(returns))
                sb.Append("\n\n**Returns:** ").Append(returns);
        }

        // Highlight the identifier token under the cursor when we can find it.
        Protocol.Range? range = null;
        var root = await document.GetSyntaxRootAsync(ct);
        var token = root?.FindToken(Math.Min(offset, Math.Max(0, text.Length - 1)));
        if (token is { } t && t.Span.Contains(Math.Min(offset, Math.Max(0, text.Length - 1))))
            range = LspConverters.ToRange(text.Lines, t.Span);

        return new Hover(new MarkupContent("markdown", sb.ToString()), range);
    }
}

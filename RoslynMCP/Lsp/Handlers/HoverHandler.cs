using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMCP.Languages;
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

    public static async Task<Hover?> HoverAsync(
        TextDocumentPositionParams p, CancellationToken ct, LanguageSession? languages = null)
    {
        if (await HandlerHelpers.ResolveAsync(p.TextDocument, p.Position, ct) is not
            var (document, text, offset) || document is null)
            return null;

        // Inside a string literal Roslyn binds to nothing, so a resource key would hover blank.
        // Ask the embedded languages first; the check ends after a syntax lookup unless the caret
        // really is in a literal, and before that when none are registered.
        if (await RoslynEmbeddedLanguages.Current.DetectAsync(document, offset, ct) is
            { Language: IEmbeddedHoverProvider embedded } embeddedContext)
        {
            return await embedded.HoverAsync(embeddedContext, ct);
        }

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, offset, ct);
        if (symbol is null)
            return null;

        // Highlight the identifier token under the cursor when we can find it.
        Protocol.Range? range = null;
        var root = await document.GetSyntaxRootAsync(ct);
        var token = root?.FindToken(Math.Min(offset, Math.Max(0, text.Length - 1)));
        if (token is { } t && t.Span.Contains(Math.Min(offset, Math.Max(0, text.Length - 1))))
            range = LspConverters.ToRange(text.Lines, t.Span);

        var markdown = new StringBuilder(Describe(symbol, ct));

        // Appended rather than merged, and after Roslyn's own description: what the pack knows is
        // where the symbol came from, which reads as provenance under the signature rather than in
        // place of it.
        foreach (var contributor in LanguageScope.Of(languages).Contributors<ILanguageHoverContributor>())
        {
            if (await contributor.HoverMarkdownAsync(symbol, document.Project, ct) is { Length: > 0 } extra)
                markdown.Append("\n\n---\n\n").Append(extra);
        }

        return new Hover(new MarkupContent("markdown", markdown.ToString()), range);
    }

    /// <summary>The signature-plus-summary markdown shown for a symbol. Shared with the markup
    /// languages, whose symbols do not come from a syntax position.</summary>
    public static string Describe(ISymbol symbol, CancellationToken ct)
    {
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

        return sb.ToString();
    }
}

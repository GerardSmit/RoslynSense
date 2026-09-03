using RoslynMCP.Languages.WebForms.Core;
using WebFormsCore;
using WebFormsCore.Nodes;
using Protocol = RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.WebForms.Lsp;

/// <summary>
/// A tag that names a registered control but carries no <c>runat="server"</c>.
/// </summary>
/// <remarks>
/// The mistake is silent by construction: without the attribute ASP.NET treats the tag as
/// literal text and writes it into the response verbatim, so the control never exists — no build
/// error, no exception, just markup in the page source. The parser models it the same way, as a
/// plain element, which is why this is a separate pass: the node that would have carried the
/// parser's own diagnostics was never created.
/// </remarks>
internal static class AspxRunatDiagnostics
{
    private const string MissingRunat = "WFR0001";
    private const string DiagnosticSource = "roslyn-sense";

    /// <summary>Severity 1 = error.</summary>
    private const int Error = 1;

    public static Protocol.Diagnostic[] Diagnostics(AspxDocument document)
    {
        if (document.Tree is not { } root)
            return [];

        List<Protocol.Diagnostic>? found = null;

        foreach (var element in AspxSymbolResolver.EnumerateElements(root))
        {
            if (TagWithoutRunat(document, element) is not { } type)
                continue;

            (found ??= []).Add(new Protocol.Diagnostic(
                AspxLanguageHandler.ToRange(
                    document, AspxSymbolResolver.Span(element.StartTag.ElementRange)),
                Error,
                MissingRunat,
                DiagnosticSource,
                $"'{element.Namespace!.Value.Value}:{element.Name.Value}' is missing runat=\"server\", "
                + $"so it renders as literal text instead of a {type.Name}."));
        }

        return found is null ? [] : [.. found];
    }

    /// <summary>
    /// The control type a plain element would have been, or <c>null</c> when the element is fine
    /// as it is. Only a prefixed tag whose prefix and name resolve to a <c>Control</c> qualifies:
    /// an unknown tag is legitimately literal, a control, collection or template node already
    /// parsed as server content — inside a collection no <c>runat</c> is needed at all — and a
    /// non-control type like <c>asp:TemplateColumn</c> or <c>asp:ListItem</c> is a collection
    /// item that never carries the attribute.
    /// </summary>
    public static Microsoft.CodeAnalysis.INamedTypeSymbol? TagWithoutRunat(
        AspxDocument document, ElementNode element)
    {
        if (element is ControlNode or CollectionNode or TemplateNode)
            return null;

        if (element.Namespace is not { } prefix)
            return null;

        var type = AspxCatalog.ResolveTag(document, prefix.Value, element.Name.Value);
        return type.IsAssignableTo("Control") ? type : null;
    }
}

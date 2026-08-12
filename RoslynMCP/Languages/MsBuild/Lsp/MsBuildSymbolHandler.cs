using Microsoft.Language.Xml;
using RoslynMCP.Languages.MsBuild.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.MsBuild.Lsp;

/// <summary>
/// The outline of a project file: its groups and targets, and what each one holds.
/// </summary>
/// <remarks>
/// Cheap, and it is what makes a four-hundred-line <c>.targets</c> navigable at all. Two levels
/// only — a group and its children — because a project file's nesting is shallow and an outline
/// that mirrored every <c>Choose</c>/<c>When</c> would be a tree of scaffolding rather than of
/// content.
/// </remarks>
internal static class MsBuildSymbolHandler
{
    public static DocumentSymbol[] Compute(string filePath)
    {
        if (MsBuildDocumentCache.Get(filePath) is not { } document)
            return [];

        if (document.Root.DescendantNodes().OfType<XmlElementSyntax>()
            .FirstOrDefault(e => e.Name.Equals("Project", StringComparison.OrdinalIgnoreCase))
            is not { } project)
        {
            return [];
        }

        var lines = document.Text.Lines;
        var symbols = new List<DocumentSymbol>();

        foreach (var group in project.Elements)
        {
            var children = new List<DocumentSymbol>();

            foreach (var child in group.Elements)
            {
                // An item is named by what it includes, a property by itself: `PackageReference`
                // fifteen times over is not an outline.
                string name = Spec(child) is { Length: > 0 } spec ? $"{child.Name} {spec}" : child.Name;

                children.Add(new DocumentSymbol(
                    name,
                    Detail(child),
                    Kind(child.Name),
                    LspConverters.ToRange(lines, child.Span.ToRoslyn()),
                    LspConverters.ToRange(lines, NameSpan(child)),
                    []));
            }

            symbols.Add(new DocumentSymbol(
                Label(group),
                null,
                Kind(group.Name),
                LspConverters.ToRange(lines, group.Span.ToRoslyn()),
                LspConverters.ToRange(lines, NameSpan(group)),
                [.. children]));
        }

        return [.. symbols];
    }

    /// <summary>A package's version, shown beside it the way a signature is beside a method.</summary>
    private static string? Detail(XmlElementBaseSyntax element) =>
        (element.GetAttributeValue("Version", null)
         ?? element.GetAttributeValue("VersionOverride", null)) is { Length: > 0 } version
            ? XmlSpans.Decode(version)
            : null;

    /// <summary>A group's condition, which is usually the only thing distinguishing two of them.</summary>
    private static string Label(XmlElementBaseSyntax element) =>
        element.GetAttributeValue("Condition", null) is { Length: > 0 } condition
            ? $"{NameOf(element)} when {XmlSpans.Decode(condition)}"
            : NameOf(element);

    private static string? Spec(XmlElementBaseSyntax element) =>
        (element.GetAttributeValue("Include", null)
         ?? element.GetAttributeValue("Update", null)
         ?? element.GetAttributeValue("Remove", null)) is { Length: > 0 } spec
            ? XmlSpans.Decode(spec)
            : null;

    private static string NameOf(XmlElementBaseSyntax element) => element switch
    {
        XmlElementSyntax e => e.Name ?? string.Empty,
        XmlEmptyElementSyntax e => e.Name ?? string.Empty,
        _ => string.Empty,
    };

    private static Microsoft.CodeAnalysis.Text.TextSpan NameSpan(XmlElementBaseSyntax element) =>
        (element switch
        {
            XmlElementSyntax { StartTag.NameNode: { } name } => name.Span,
            XmlEmptyElementSyntax { NameNode: { } name } => name.Span,
            _ => element.Span,
        }).ToRoslyn();

    private static int Kind(string name) => name switch
    {
        "PropertyGroup" => LspSymbolKind.Namespace,
        "ItemGroup" => LspSymbolKind.Array,
        "Target" => LspSymbolKind.Method,
        "Import" => LspSymbolKind.Module,
        "Choose" or "When" or "Otherwise" => LspSymbolKind.Namespace,
        _ => LspSymbolKind.Property,
    };
}

using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Languages.WebForms.Lsp;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using WebFormsCore.Models;
using WebFormsCore.Nodes;

namespace RoslynMCP.Languages.WebForms;

/// <summary>
/// textDocument/documentLink for markup: the files a page names — its master page, a user
/// control's <c>Src</c>, a script, a stylesheet — become Ctrl-clickable.
/// </summary>
/// <remarks>
/// A link is emitted only for a target that is on disk. The alternative is underlining every
/// path-shaped attribute and letting the editor fail on the click, which is a worse answer than
/// no link: a value the server cannot resolve is usually a CDN URL, a runtime-substituted path,
/// or a typo, and none of those is something the user wants to open.
/// </remarks>
internal sealed partial class WebFormsLanguage : ILanguageDocumentLinkProvider
{
    /// <summary>
    /// Directive attributes whose value is a path to another file in the project. The same set
    /// go-to-definition already navigates, so Ctrl-click and F12 agree on what is a file.
    /// </summary>
    private static readonly string[] s_directivePathAttributes =
        ["MasterPageFile", "Src", "CodeBehind", "CodeFile", "VirtualPath"];

    public async Task<DocumentLink[]> DocumentLinksAsync(
        DocumentLinkParams p, CancellationToken ct)
    {
        var document = await AspxDocumentService.GetAsync(
            LspConverters.UriToPath(p.TextDocument.Uri), ct);
        if (document?.Tree is not { } root)
            return [];

        var links = new List<DocumentLink>();

        foreach (var directive in root.Directives)
        {
            foreach (var (key, value) in directive.Attributes)
            {
                if (s_directivePathAttributes.Contains(key.Value, StringComparer.OrdinalIgnoreCase))
                    AddLink(document, links, value);
            }
        }

        foreach (var element in AspxSymbolResolver.EnumerateElements(root))
        {
            ct.ThrowIfCancellationRequested();

            if (PathAttributeOf(element) is not { } attributeName)
                continue;

            foreach (var (key, value) in element.RawAttributes)
            {
                if (key.Value.Equals(attributeName, StringComparison.OrdinalIgnoreCase))
                    AddLink(document, links, value);
            }
        }

        return [.. links];
    }

    /// <summary>The attribute that names a file on this tag, if it has one.</summary>
    private static string? PathAttributeOf(ElementNode element) =>
        element.StartTag.Name.Value.ToLowerInvariant() switch
        {
            "script" => "src",
            "link" => "href",
            _ => null,
        };

    private static void AddLink(AspxDocument document, List<DocumentLink> links, AttributeValue value)
    {
        if (Resolve(document, value) is not { } target)
            return;

        links.Add(new DocumentLink(
            AspxLanguageHandler.ToRange(document, AspxSymbolResolver.Span(value.Range)),
            LspConverters.PathToUri(target),
            target));
    }

    /// <summary>
    /// The file an attribute value points at, or <c>null</c> when it points at nothing this
    /// server can open. <c>~/</c> and a leading slash are both the application root, which for a
    /// project-based workspace is the directory holding the project file; everything else is
    /// relative to the markup file itself.
    /// </summary>
    private static string? Resolve(AspxDocument document, AttributeValue value)
    {
        // `<%# … %>` and `<%$ … %>` in the attribute are both resolved at runtime rather than
        // being paths. The builder has to be excluded by kind: the value it reports is its
        // resource key, so `src="<%$ AppSettings: CdnRoot %>"` would otherwise be a candidate
        // whose path is `CdnRoot` — and File.Exists hiding that is luck, not correctness.
        if (value.Kind is not AttributeValueKind.Literal)
            return null;

        string path = value.Value;
        if (string.IsNullOrWhiteSpace(path)
            || path.Contains("://", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }

        // Cache-busting query strings and fragments are part of the URL, never of the file name.
        int cut = path.AsSpan().IndexOfAny('?', '#');
        if (cut >= 0)
            path = path[..cut];

        string relative = path.Replace('/', Path.DirectorySeparatorChar).Trim();
        string? baseDirectory;

        if (relative.StartsWith("~" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            baseDirectory = Path.GetDirectoryName(document.Project.FilePath);
            relative = relative[2..];
        }
        else if (relative.StartsWith(Path.DirectorySeparatorChar))
        {
            baseDirectory = Path.GetDirectoryName(document.Project.FilePath);
            relative = relative[1..];
        }
        else
        {
            baseDirectory = Path.GetDirectoryName(document.FilePath);
        }

        if (string.IsNullOrEmpty(baseDirectory) || relative.Length == 0)
            return null;

        try
        {
            string full = Path.GetFullPath(Path.Combine(baseDirectory, relative));
            return File.Exists(full) ? full : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Services;
using RoslynMCP.Tools;

namespace RoslynMCP.Languages.WebForms.Tools;

/// <summary>
/// Resolves symbols in ASPX files by mapping the caller's marked snippet to an offset and asking
/// the same resolver the editor's go-to-definition uses.
/// </summary>
internal class AspxGoToDefinition(IOutputFormatter fmt) : IGoToDefinitionHandler
{
    public bool CanHandle(string filePath) => AspxDocumentService.IsAspxFile(filePath);

    public async Task<string> ResolveAsync(
        string systemPath, string markupSnippet, int contextLines, CancellationToken cancellationToken)
    {
        if (!MarkupString.TryParse(markupSnippet, out var markup, out string? parseError))
            return $"Error: Invalid markup snippet. {parseError}";

        var document = await AspxDocumentService.GetAsync(systemPath, cancellationToken);
        if (document is null)
        {
            return $"Error: Couldn't load '{Path.GetFileName(systemPath)}'. " +
                   "The file must exist and belong to a project that produces a compilation.";
        }

        var symbol = AspxSourceMappingService.FindMarkedSpan(document.Text, markup!) is { } marked
            ? AspxSymbolResolver.ResolveAt(document, marked.Start)?.Symbol
            : null;

        if (symbol is null)
            return $"No symbol found for '{markup!.MarkedText}' in ASPX file.";

        return await GoToDefinitionSnippetTool.FormatDefinitionAsync(
            symbol, document.Project, contextLines, fmt, cancellationToken);
    }
}

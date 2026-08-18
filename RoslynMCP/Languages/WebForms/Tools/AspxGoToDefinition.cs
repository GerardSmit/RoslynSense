using System.Text;
using RoslynMCP.Languages;
using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Languages.WebForms.Lsp;
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

        var marked = AspxSourceMappingService.FindMarkedSpan(document.Text, markup!);
        var hit = marked is { } span ? AspxSymbolResolver.ResolveAt(document, span.Start) : null;

        // A template-nested ID has no code-behind field, so the FindControl("id") call sites are
        // the definition answer — the same one the editor's F12 gives for this caret.
        if (hit is { Kind: AspxHitKind.ControlId, Symbol: null, Name: { Length: > 0 } controlId })
        {
            var project = await AspxDocumentService.CurrentProjectAsync(document, cancellationToken);
            var wrappers = await ProjectIndexCacheService.GetFindControlWrappersAsync(
                project, cancellationToken);
            var references = await AspxSourceMappingService.FindControlByIdAsync(
                project, controlId, wrappers, cancellationToken);

            if (references.Count > 0)
                return FormatFindControlCallSites(controlId, references);
        }

        // A resource key inside a <% %> block is not a symbol and never will be: literals bind to
        // nothing. Asked here for the same reason the C# tool asks before its own symbol answer,
        // so a session and the editor's F12 resolve the same caret the same way.
        if (marked is { } literal
            && await AspxLanguageHandler.ProjectedEmbeddedAsync(
                document, literal.Start, cancellationToken) is
                { Language: IEmbeddedDefinitionProvider embedded } embeddedContext
            && await embedded.DefinitionAsync(embeddedContext, typeDefinition: false, cancellationToken) is
                { Length: > 0 } locations)
        {
            return await GoToDefinitionSnippetTool.FormatEmbeddedLocationsAsync(
                locations, contextLines, cancellationToken);
        }

        if (hit?.Symbol is not { } symbol)
            return $"No symbol found for '{markup!.MarkedText}' in ASPX file.";

        return await GoToDefinitionSnippetTool.FormatDefinitionAsync(
            symbol, document.Project, contextLines, fmt, cancellationToken);
    }

    private string FormatFindControlCallSites(
        string controlId, List<AspxSymbolReference> references)
    {
        var sb = new StringBuilder();

        fmt.AppendHeader(sb, $"FindControl call sites for '{controlId}'");
        fmt.AppendField(sb, "Note",
            "Control is inside a template — no code-behind field; it is reached via "
            + "FindControl at runtime, so its call sites are the code half of the declaration");
        fmt.AppendSeparator(sb);

        var rows = new List<string[]>();
        foreach (var reference in references)
        {
            string snippet = reference.CodeSnippet.Length > 80
                ? reference.CodeSnippet[..77] + "..."
                : reference.CodeSnippet;
            rows.Add([reference.FilePath, $"{reference.Line}", snippet]);
        }

        fmt.AppendTable(sb, "FindControl Calls", ["File", "Line", "Snippet"], rows);

        return sb.ToString();
    }
}

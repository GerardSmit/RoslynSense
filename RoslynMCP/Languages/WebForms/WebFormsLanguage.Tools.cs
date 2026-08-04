using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Languages.WebForms.Tools;
using RoslynMCP.Services;
using RoslynMCP.Tools;

namespace RoslynMCP.Languages.WebForms;

/// <summary>
/// The MCP side of the pack. The tools ask for <c>IEnumerable&lt;I*Handler&gt;</c> and know
/// nothing about packs, so the pack implements those interfaces and forwards; that way one
/// registration gate — <c>settings.WebForms</c> — governs the editor features and the AI tools
/// together instead of each having its own.
/// </summary>
internal sealed partial class WebFormsLanguage :
    IGoToDefinitionHandler,
    IFindUsagesHandler,
    IOutlineHandler,
    IRenameHandler,
    IDiagnosticsHandler
{
    private AspxGoToDefinition _goToDefinition = null!;
    private AspxFindUsages _findUsages = null!;
    private readonly AspxOutline _outline = new();
    private readonly AspxRename _rename = new();
    private readonly AspxDiagnostics _diagnostics = new();

    private void InitializeToolHandlers(IOutputFormatter formatter)
    {
        _goToDefinition = new AspxGoToDefinition(formatter);
        _findUsages = new AspxFindUsages(formatter);
    }

    public bool CanHandle(string filePath) => AspxDocumentService.IsAspxFile(filePath);

    public Task<string> ResolveAsync(
        string systemPath, string markupSnippet, int contextLines, CancellationToken cancellationToken) =>
        _goToDefinition.ResolveAsync(systemPath, markupSnippet, contextLines, cancellationToken);

    public Task<string> FindUsagesAsync(
        string systemPath, string markupSnippet, int maxResults,
        CancellationToken cancellationToken, int? hintLine = null) =>
        _findUsages.FindUsagesAsync(systemPath, markupSnippet, maxResults, cancellationToken, hintLine);

    public Task<string> GetOutlineAsync(string systemPath, CancellationToken cancellationToken) =>
        _outline.GetOutlineAsync(systemPath, cancellationToken);

    public Task<List<RenameChangedFile>> UpdateReferencesAsync(
        Project project, Solution solution, ISymbol symbol,
        string oldName, string newName, CancellationToken cancellationToken) =>
        _rename.UpdateReferencesAsync(project, solution, symbol, oldName, newName, cancellationToken);

    public Task<string> ValidateAsync(
        string systemPath, IOutputFormatter fmt, CancellationToken cancellationToken) =>
        _diagnostics.ValidateAsync(systemPath, fmt, cancellationToken);
}

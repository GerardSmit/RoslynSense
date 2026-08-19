using RoslynMCP.Languages.MsBuild.Lsp;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.MsBuild;

/// <summary>Quick fixes on a package reference.</summary>
internal sealed partial class MsBuildLanguage : ILanguageCodeActionProvider
{
    public Task<CodeAction[]> CodeActionsAsync(CodeActionParams p, CancellationToken ct) =>
        Task.FromResult(MsBuildCodeActionHandler.Compute(p));

    /// <summary>
    /// Actions arrive complete, for the same reason completion items do.
    /// </summary>
    /// <remarks>
    /// A resolve request carries no document, and the resolve payload has no room for a pack id, so
    /// it cannot be routed back here. That is affordable because the edit is a span and a string —
    /// the expensive half is the version list, which was already fetched to know the action was
    /// worth offering.
    /// </remarks>
    public Task<CodeAction> ResolveCodeActionAsync(CodeAction action, CancellationToken ct) =>
        Task.FromResult(action);
}

/// <summary>Hover on a package, a property or an element.</summary>
internal sealed partial class MsBuildLanguage : ILanguageHoverProvider
{
    public Task<Hover?> HoverAsync(TextDocumentPositionParams p, CancellationToken ct) =>
        MsBuildHoverHandler.ComputeAsync(p, ct);
}

/// <summary>F12 to where a version is really set, or to the file an Import names.</summary>
internal sealed partial class MsBuildLanguage : ILanguageDefinitionProvider
{
    public Task<Location[]> DefinitionAsync(
        TextDocumentPositionParams p, bool typeDefinition, CancellationToken ct) =>
        // No type definition in a project file: there are no types, and answering the same thing
        // for both would make Go To Type Definition silently mean something else.
        Task.FromResult(typeDefinition ? [] : MsBuildNavigationHandler.Compute(p));
}

/// <summary>The outline of a project file.</summary>
internal sealed partial class MsBuildLanguage : ILanguageDocumentSymbolProvider
{
    public Task<DocumentSymbol[]> DocumentSymbolAsync(DocumentSymbolParams p, CancellationToken ct) =>
        Task.FromResult(MsBuildSymbolHandler.Compute(LspConverters.UriToPath(p.TextDocument.Uri)));
}

using RoslynMCP.Languages.WebForms.Lsp;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.WebForms;

internal sealed partial class WebFormsLanguage :
    ILanguageCompletionProvider,
    ILanguageCodeActionProvider,
    ILanguageCommandProvider
{
    public Task<CompletionList> CompletionAsync(
        CompletionParams p, LspResolveCache cache, CancellationToken ct) =>
        AspxCompletionHandler.CompletionAsync(p, cache, ct);

    /// <summary>
    /// Markup items are complete as sent — a tag name, an attribute name, an ID. The ones that
    /// come from inline C# are produced by the C# completion handler against the projection and
    /// carry its cache data, so they resolve through it rather than through here.
    /// </summary>
    public Task<CompletionItem> ResolveCompletionAsync(
        CompletionItem item, LspResolveCache cache, CancellationToken ct) =>
        Task.FromResult(item);

    public Task<CodeAction[]> CodeActionsAsync(CodeActionParams p, CancellationToken ct) =>
        AspxCodeActionHandler.CodeActionsAsync(p, ct);

    /// <summary>The markup half of every action is a span and a string, computed up front; the
    /// code-behind half arrives from <see cref="ExecuteCommandAsync"/>.</summary>
    public Task<CodeAction> ResolveCodeActionAsync(CodeAction action, CancellationToken ct) =>
        Task.FromResult(action);

    public bool CanExecute(string command) =>
        command == ExecuteCommandHandler.GenerateEventHandlerCommand;

    public Task<object> ExecuteCommandAsync(ExecuteCommandParams p, CancellationToken ct) =>
        AspxEventCommand.ExecuteAsync(p, ct);
}

using RoslynMCP.Languages.Proto.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.Proto;

internal sealed partial class ProtoLanguage : ILanguageCodeActionProvider
{
    public Task<CodeAction[]> CodeActionsAsync(CodeActionParams p, CancellationToken ct) =>
        ProtoCodeActionHandler.CodeActionsAsync(p, ct);

    /// <summary>
    /// Returns the action unchanged. An import is a line of text at an offset the parse already
    /// gave up, and the build is a command the client hands back rather than an edit — neither has
    /// a half worth computing later, so neither carries the <c>data</c> a resolve would need.
    /// </summary>
    public Task<CodeAction> ResolveCodeActionAsync(CodeAction action, CancellationToken ct) =>
        Task.FromResult(action);
}

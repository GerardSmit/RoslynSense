using RoslynMCP.Languages.Proto.Lsp;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.Proto;

internal sealed partial class ProtoLanguage : ILanguageCompletionProvider
{
    /// <summary>
    /// The resolve cache is not passed on. It exists to keep a Roslyn completion item alive between
    /// the list and the resolve that fills in its documentation and its edits; nothing in a
    /// <c>.proto</c> costs enough to defer, so every item leaves here finished and the cache would
    /// only hold things nobody comes back for.
    /// </summary>
    public Task<CompletionList> CompletionAsync(
        CompletionParams p, LspResolveCache cache, CancellationToken ct) =>
        ProtoCompletionHandler.CompletionAsync(p, ct);

    /// <summary>
    /// Returns the item unchanged, and in practice is never called: an item that carries no
    /// <c>data</c> gives a client nothing to resolve against, which is the contract
    /// <see cref="ILanguageCompletionProvider"/> asks a self-contained pack to hold to.
    /// </summary>
    public Task<CompletionItem> ResolveCompletionAsync(
        CompletionItem item, LspResolveCache cache, CancellationToken ct) =>
        Task.FromResult(item);
}

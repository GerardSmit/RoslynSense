using RoslynMCP.Languages.MsBuild.Lsp;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.MsBuild;

/// <summary>Completion in a project file.</summary>
internal sealed partial class MsBuildLanguage : ILanguageCompletionProvider
{
    public async Task<CompletionList> CompletionAsync(
        CompletionParams p, LspResolveCache cache, CancellationToken ct) =>
        await MsBuildCompletionHandler.CompleteAsync(p, ct) ?? Nothing;

    /// <summary>
    /// Items arrive complete, so this hands the item back untouched.
    /// </summary>
    /// <remarks>
    /// Not a shortcut — a constraint. <c>completionItem/resolve</c> carries no document, and the
    /// resolve payload the protocol layer defines is a cache generation and an index with no room
    /// for a pack id, so a resolve request cannot be routed back here to be answered. Everything an
    /// item needs is therefore sent with it, which is affordable because the expensive half —
    /// package descriptions and version lists — is already fetched to build the list at all.
    /// </remarks>
    public Task<CompletionItem> ResolveCompletionAsync(
        CompletionItem item, LspResolveCache cache, CancellationToken ct) =>
        Task.FromResult(item);

    /// <summary>
    /// What "no completions" is on the wire.
    /// </summary>
    /// <remarks>
    /// An empty list with <c>isIncomplete: false</c> is how LSP says "nothing here, stop asking".
    /// It matters that this is not reached by returning an empty <em>item array</em> from a place
    /// that had something to say: VS Code falls back to word-based completion when a list comes
    /// back empty, and in a project file that means offering XML tag names and version fragments
    /// scraped out of the buffer.
    /// </remarks>
    private static CompletionList Nothing { get; } = new(false, []);
}

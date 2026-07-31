namespace RoslynMCP.Lsp;

/// <summary>
/// Client capabilities the static handlers need. Static because the handlers are; when two
/// editors share one daemon the last initialize wins, which is safe in practice because the
/// flags here are conservative defaults that only add richness (snippets) rather than change
/// semantics.
/// </summary>
internal static class LspClientState
{
    /// <summary>Whether completion items may carry <c>$0</c>-style tab stops.</summary>
    public static bool SnippetSupport { get; set; }
}

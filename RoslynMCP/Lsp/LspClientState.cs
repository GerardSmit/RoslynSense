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

    /// <summary>
    /// Whether the client reads <c>editRange</c> off <c>CompletionList.itemDefaults</c>. When it
    /// does, the range every item in a list replaces is sent once instead of a thousand times and
    /// the items carry no <c>textEdit</c> at all — the bulk of a completion response. Off (the
    /// default, and what any client that stays silent gets) every item keeps its own edit.
    /// </summary>
    public static bool CompletionEditRangeDefault { get; set; }
}

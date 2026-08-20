using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.Values;

/// <summary>
/// Completion inside a bound literal: the values the set actually has.
/// </summary>
/// <remarks>
/// The half of this feature that gets used every day. The diagnostic catches the mistake after it
/// is made; this is what stops it being made, and it is also the only way to <i>discover</i> the
/// codes at all without opening a database client and remembering the table name.
/// <para>
/// Offered even when the set is partial or came back with a problem. A list that might be missing
/// entries is still better than no list, and unlike the diagnostic it makes no claim about what is
/// not in it.
/// </para>
/// </remarks>
internal sealed partial class ValuesLanguage : IEmbeddedCompletionProvider
{
    public async Task<CompletionList> CompletionAsync(
        EmbeddedStringContext context, CompletionParams p, CancellationToken ct)
    {
        if (Site(context, ct) is not { } site)
            return new CompletionList(false, []);

        var contents = await _catalog.ContentsAsync(site.Set, ct);

        if (contents.Values.IsEmpty)
            return new CompletionList(false, []);

        var text = await context.Document.GetTextAsync(ct);

        // The literal's content, quotes excluded: committing an item replaces whatever is written
        // rather than splicing into it, which is what makes fixing a typo one keystroke.
        var range = LspConverters.ToRange(text.Lines, site.Span);

        var items = new List<CompletionItem>(contents.Values.Length);

        for (int i = 0; i < contents.Values.Length; i++)
        {
            var entry = contents.Values[i];

            items.Add(new CompletionItem(
                entry.Value,
                LspCompletionItemKind.EnumMember,
                entry.Label,
                // The query's own order, kept. A lookup table is usually ordered by something that
                // means something — a sort column, a workflow — and re-sorting it alphabetically
                // throws that away for no gain.
                i.ToString("D5"),
                entry.Value,
                new TextEdit(range, entry.Value)));
        }

        return new CompletionList(false, [.. items]);
    }
}

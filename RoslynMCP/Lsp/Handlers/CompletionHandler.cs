using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp.Completion;
using RoslynMCP.Lsp.Protocol;
using CompletionItem = RoslynMCP.Lsp.Protocol.CompletionItem;
using CompletionList = RoslynMCP.Lsp.Protocol.CompletionList;
using RoslynCompletionOptions = Microsoft.CodeAnalysis.Completion.CompletionOptions;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>
/// textDocument/completion backed by Roslyn's <see cref="CompletionService"/>. Two things
/// make the list match VS quality (both internal, reached via Publicizer — see csproj):
/// - the internal GetCompletionsAsync overload taking <see cref="RoslynCompletionOptions"/>,
///   with ShowItemsFromUnimportedNamespaces enabled (import completion — types you haven't
///   'using'-imported yet, with the using added on commit via resolve)
/// - Document.WithFrozenPartialSemantics: completion binds against whatever compilation
///   state exists instead of forcing a full bind; slow binds starve Roslyn's per-provider
///   time budgets and collapse the list to locals/keywords.
///
/// Roslyn decides <em>what</em> is in scope; ordering is ours (see
/// <see cref="RoslynMCP.Lsp.Completion.CompletionRanker"/>): a CamelHumps match feeds a 64-bit
/// relevance word whose bit order is the ranking, so locals beat members beat types, obsolete
/// and unimported items sink, and the whole thing is re-decided per keystroke.
/// </summary>
internal static class CompletionHandler
{
    private const int MaxItems = 1000;

    private static readonly RoslynCompletionOptions s_options = RoslynCompletionOptions.Default with
    {
        ShowItemsFromUnimportedNamespaces = true,
        TriggerOnTypingLetters = true,
        // The unimported-type index is normally populated lazily in the background, so the
        // first requests silently miss all import-completion items. Force it instead: one-time
        // cost per project, then cached.
        ForceExpandedCompletionIndexCreation = true,
        UpdateImportCompletionCacheInBackground = true,
    };

    public static async Task<CompletionList> CompletionAsync(
        CompletionParams p, LspResolveCache cache, CancellationToken ct)
    {
        if (await HandlerHelpers.ResolveAsync(p.TextDocument, p.Position, ct) is not
            var (document, text, offset) || document is null)
            return new CompletionList(false, Array.Empty<CompletionItem>());

        document = document.WithFrozenPartialSemantics(ct);

        var service = CompletionService.GetService(document);
        if (service is null)
            return new CompletionList(false, Array.Empty<CompletionItem>());

        var trigger = p.Context is { TriggerKind: 2, TriggerCharacter.Length: > 0 } context
            ? CompletionTrigger.CreateInsertionTrigger(context.TriggerCharacter[0])
            : CompletionTrigger.Invoke;

        // Let Roslyn's per-provider heuristics veto character triggers (e.g. "<" that is a
        // less-than operator) instead of answering every trigger with a full list.
        if (trigger.Kind == CompletionTriggerKind.Insertion
            && !service.ShouldTriggerCompletion(text, offset, trigger))
            return new CompletionList(false, Array.Empty<CompletionItem>());

        var completions = await service.GetCompletionsAsync(
            document, offset, s_options, document.Project.Solution.Options, trigger,
            roles: null, cancellationToken: ct);
        if (completions.ItemsList.Count == 0)
            return new CompletionList(false, Array.Empty<CompletionItem>());

        // The span Roslyn wants replaced by the committed item (usually the partial word).
        var defaultRange = LspConverters.ToRange(text.Lines, completions.Span);

        string prefix = completions.Span.Length > 0 && completions.Span.End <= text.Length
            ? text.ToString(completions.Span)
            : "";
        string contextId = CompletionRanker.ContextId(text, completions.Span);

        // Declaring types and the nearest local — the two ranking inputs a completion item does
        // not carry. One pass over the type being completed on, not a symbol resolve per item.
        var semantics = await CompletionSemanticContext.CreateAsync(document, completions.Span.Start, ct);

        var ranked = CompletionRanker.Rank(completions.ItemsList, prefix, contextId, MaxItems, semantics);
        if (ranked.Items.Count == 0)
            return new CompletionList(false, Array.Empty<CompletionItem>());

        var cachedItems = ranked.Items.Select(r => r.Item).ToList();
        long cacheId = cache.StoreCompletions(document, cachedItems);

        var items = ranked.Items
            .Select((entry, index) =>
            {
                var item = entry.Item;
                return new CompletionItem(
                    item.DisplayText,
                    ToLspKind(item),
                    Detail(item),
                    entry.SortText(index),
                    FilterText(item, prefix),
                    // Symbol items store their real commit text in Properties (e.g. generic
                    // types commit "List" while displaying "List<>").
                    new TextEdit(defaultRange,
                        item.Properties.TryGetValue("InsertionText", out string? insertion)
                            ? insertion
                            : item.DisplayText))
                {
                    Data = new CompletionItemData(cacheId, index),
                    Preselect = item.Rules.MatchPriority == MatchPriority.Preselect ? true : null,
                    Command = new Command(
                        "",
                        ExecuteCommandHandler.CompletionAcceptedCommand,
                        [contextId, CompletionStatistics.Identity(item)]),
                };
            })
            .ToArray();

        // Ranking (and the typo tier) depends on the typed prefix, so a narrowed list is not a
        // subset the client can compute on its own — ask for a fresh request per keystroke.
        return new CompletionList(ranked.Truncated || prefix.Length > 0, items);
    }

    /// <summary>
    /// Hands the client a filter text that begins with exactly what the user typed, so that its
    /// own fuzzy score is the same for every item and cannot re-order the list.
    /// </summary>
    /// <remarks>
    /// VS Code sorts by <c>score → wordDistance → index-in-sortText-order</c>, and the score is
    /// computed against filterText: leaving the plain name there lets the client's notion of a
    /// good match override the ranking computed here (a camel-hump hit scores below a literal
    /// prefix hit however relevant it is). Prepending the typed text equalises the score, which
    /// hands the decision back to sortText. Highlighting survives because the client rescores the
    /// <em>label</em> separately for highlight positions. The rest of the name stays in the filter
    /// text so the item still matches while the next keystroke's request is in flight, instead of
    /// the list blanking out between requests.
    /// </remarks>
    private static string FilterText(Microsoft.CodeAnalysis.Completion.CompletionItem item, string prefix)
    {
        string filterText = string.IsNullOrEmpty(item.FilterText) ? item.DisplayText : item.FilterText;
        return prefix.Length == 0 ? filterText : prefix + filterText;
    }

    private static string? Detail(Microsoft.CodeAnalysis.Completion.CompletionItem item)
    {
        // Import-completion items carry the namespace they come from — showing it is the
        // signal that committing will add a using.
        if (!string.IsNullOrEmpty(item.InlineDescription))
            return item.InlineDescription;
        return item.Properties.TryGetValue("Namespace", out string? ns) && ns.Length > 0
            ? ns : null;
    }

    /// <summary>completionItem/resolve: documentation + the real committed edit. Items whose
    /// commit is more than "insert the label" (import completion adding a using, override
    /// stubs, …) get their extra edits as additionalTextEdits here.</summary>
    public static async Task<CompletionItem> ResolveAsync(
        CompletionItem item, LspResolveCache cache, CancellationToken ct)
    {
        if (item.Data is null || cache.GetCompletion(item.Data.CacheId, item.Data.Index) is not
            var (document, roslynItem) || document is null)
            return item;

        var service = CompletionService.GetService(document);
        if (service is null)
            return item;

        // The real committed change: the using directive an import completion adds, and — for
        // override/interface completions — the generated member body, which is nothing like
        // the label the initial pass proposed.
        if (roslynItem.IsComplexTextEdit || roslynItem.Flags.HasFlag(CompletionItemFlags.Expanded))
        {
            try
            {
                var change = await service.GetChangeAsync(document, roslynItem, cancellationToken: ct);
                var text = await document.GetTextAsync(ct);

                var extra = change.TextChanges
                    .Where(c => !c.Span.IntersectsWith(roslynItem.Span))
                    .Select(c => new TextEdit(LspConverters.ToRange(text.Lines, c.Span), c.NewText ?? ""))
                    .ToArray();
                if (extra.Length > 0)
                    item = item with { AdditionalTextEdits = extra };

                var main = change.TextChanges
                    .Where(c => c.Span.IntersectsWith(roslynItem.Span))
                    .ToList();
                if (main.Count == 1)
                    item = WithCommittedEdit(item, main[0], change.NewPosition ?? -1, text);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* best effort — the plain insertion still works */ }
        }

        var description = await service.GetDescriptionAsync(document, roslynItem, ct);
        if (description is not null && !description.TaggedParts.IsEmpty)
            item = item with { Documentation = new MarkupContent("markdown", description.Text) };

        return item;
    }

    /// <summary>
    /// Replaces the item's placeholder edit with what Roslyn actually commits, and — when the
    /// client understands snippets — turns Roslyn's post-commit caret position into a <c>$0</c>
    /// tab stop. That is what leaves the caret inside a generated override body rather than
    /// after the closing brace.
    /// </summary>
    private static CompletionItem WithCommittedEdit(
        CompletionItem item, TextChange change, int newPosition, SourceText text)
    {
        string newText = change.NewText ?? "";
        var range = LspConverters.ToRange(text.Lines, change.Span);

        int caret = newPosition - change.Span.Start;
        if (!LspClientState.SnippetSupport || caret < 0 || caret > newText.Length)
            return item with { TextEdit = new TextEdit(range, newText), InsertTextFormat = LspInsertTextFormat.PlainText };

        string snippet = EscapeSnippet(newText[..caret]) + "$0" + EscapeSnippet(newText[caret..]);
        return item with
        {
            TextEdit = new TextEdit(range, snippet),
            InsertTextFormat = LspInsertTextFormat.Snippet,
        };
    }

    private static string EscapeSnippet(string value) =>
        value.Replace("\\", "\\\\").Replace("$", "\\$").Replace("}", "\\}");

    private static int ToLspKind(Microsoft.CodeAnalysis.Completion.CompletionItem item)
    {
        foreach (var tag in item.Tags)
        {
            switch (tag)
            {
                case "Method" or "ExtensionMethod": return LspCompletionItemKind.Method;
                case "Property": return LspCompletionItemKind.Property;
                case "Field": return LspCompletionItemKind.Field;
                case "Event": return LspCompletionItemKind.Event;
                case "Class": return LspCompletionItemKind.Class;
                case "Interface": return LspCompletionItemKind.Interface;
                case "Structure": return LspCompletionItemKind.Struct;
                case "Enum": return LspCompletionItemKind.Enum;
                case "EnumMember": return LspCompletionItemKind.EnumMember;
                case "Delegate": return LspCompletionItemKind.Function;
                case "Namespace": return LspCompletionItemKind.Module;
                case "Local" or "Parameter" or "RangeVariable": return LspCompletionItemKind.Variable;
                case "Constant": return LspCompletionItemKind.Constant;
                case "Keyword": return LspCompletionItemKind.Keyword;
                case "Snippet": return LspCompletionItemKind.Snippet;
                case "Operator": return LspCompletionItemKind.Operator;
                case "TypeParameter": return LspCompletionItemKind.TypeParameter;
            }
        }
        return LspCompletionItemKind.Text;
    }
}

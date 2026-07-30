using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
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

        // The client re-queries an isIncomplete list as the user types, but plain
        // Take(MaxItems) on an alphabetical list makes everything after the cap unreachable
        // ("StringBuilder" never surfaces from 592 type items). Rank by the typed prefix
        // (CamelHumps-aware via Roslyn's PatternMatcher) before capping.
        IEnumerable<Microsoft.CodeAnalysis.Completion.CompletionItem> ordered = completions.ItemsList;
        string prefix = completions.Span.Length > 0 && completions.Span.End <= text.Length
            ? text.ToString(completions.Span)
            : "";
        if (completions.ItemsList.Count > MaxItems && prefix.Length > 0)
        {
            ordered = completions.ItemsList
                .OrderBy(item => MatchRank(item.FilterText, prefix))
                .ThenBy(item => item.SortText, StringComparer.OrdinalIgnoreCase);
        }

        var cachedItems = ordered.Take(MaxItems).ToList();
        long cacheId = cache.StoreCompletions(document, cachedItems);

        var items = cachedItems
            .Select((item, index) => new CompletionItem(
                item.DisplayText,
                ToLspKind(item),
                Detail(item),
                item.SortText,
                item.FilterText,
                // Symbol items store their real commit text in Properties (e.g. generic
                // types commit "List" while displaying "List<>").
                new TextEdit(defaultRange,
                    item.Properties.TryGetValue("InsertionText", out string? insertion)
                        ? insertion
                        : item.DisplayText))
            {
                Data = new CompletionItemData(cacheId, index),
                Preselect = item.Rules.MatchPriority == MatchPriority.Preselect ? true : null,
            })
            .ToArray();

        return new CompletionList(completions.ItemsList.Count > MaxItems, items);
    }

    private static int MatchRank(string candidate, string pattern)
    {
        if (candidate.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (candidate.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            return 1;
        return 2;
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

        // Additional edits (e.g. the using directive) — everything the real completion
        // change touches outside the item's own replacement span.
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
            }
            catch (OperationCanceledException) { throw; }
            catch { /* best effort — the plain insertion still works */ }
        }

        var description = await service.GetDescriptionAsync(document, roslynItem, ct);
        if (description is not null && !description.TaggedParts.IsEmpty)
            item = item with { Documentation = new MarkupContent("markdown", description.Text) };

        return item;
    }

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

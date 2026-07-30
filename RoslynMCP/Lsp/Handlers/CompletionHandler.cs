using Microsoft.CodeAnalysis.Completion;
using RoslynMCP.Lsp.Protocol;
using CompletionItem = RoslynMCP.Lsp.Protocol.CompletionItem;
using CompletionList = RoslynMCP.Lsp.Protocol.CompletionList;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>textDocument/completion backed by Roslyn's <see cref="CompletionService"/>
/// (available because Microsoft.CodeAnalysis.CSharp.Features is referenced).</summary>
internal static class CompletionHandler
{
    private const int MaxItems = 1000;

    public static async Task<CompletionList> CompletionAsync(
        CompletionParams p, LspResolveCache cache, CancellationToken ct)
    {
        if (await HandlerHelpers.ResolveAsync(p.TextDocument, p.Position, ct) is not
            var (document, text, offset) || document is null)
            return new CompletionList(false, Array.Empty<CompletionItem>());

        var service = CompletionService.GetService(document);
        if (service is null)
            return new CompletionList(false, Array.Empty<CompletionItem>());

        var completions = await service.GetCompletionsAsync(document, offset, cancellationToken: ct);
        if (completions.ItemsList.Count == 0)
            return new CompletionList(false, Array.Empty<CompletionItem>());

        // The span Roslyn wants replaced by the committed item (usually the partial word).
        var defaultRange = LspConverters.ToRange(text.Lines, completions.Span);

        var cachedItems = completions.ItemsList.Take(MaxItems).ToList();
        long cacheId = cache.StoreCompletions(document, cachedItems);

        var items = cachedItems
            .Select((item, index) => new CompletionItem(
                item.DisplayText,
                ToLspKind(item),
                string.IsNullOrEmpty(item.InlineDescription) ? null : item.InlineDescription,
                item.SortText,
                item.FilterText,
                new TextEdit(defaultRange, item.DisplayText))
            {
                Data = new CompletionItemData(cacheId, index),
            })
            .ToArray();

        return new CompletionList(completions.ItemsList.Count > MaxItems, items);
    }

    /// <summary>completionItem/resolve: attaches documentation to the selected item.</summary>
    public static async Task<CompletionItem> ResolveAsync(
        CompletionItem item, LspResolveCache cache, CancellationToken ct)
    {
        if (item.Data is null || cache.GetCompletion(item.Data.CacheId, item.Data.Index) is not
            var (document, roslynItem) || document is null)
            return item;

        var service = CompletionService.GetService(document);
        if (service is null)
            return item;

        var description = await service.GetDescriptionAsync(document, roslynItem, ct);
        if (description is null || description.TaggedParts.IsEmpty)
            return item;

        return item with { Documentation = new MarkupContent("markdown", description.Text) };
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

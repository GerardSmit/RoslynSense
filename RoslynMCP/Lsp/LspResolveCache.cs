using Microsoft.CodeAnalysis;
using RoslynCodeAction = Microsoft.CodeAnalysis.CodeActions.CodeAction;
using RoslynCompletionItem = Microsoft.CodeAnalysis.Completion.CompletionItem;

namespace RoslynMCP.Lsp;

/// <summary>
/// Per-session cache backing the lazy resolve endpoints (completionItem/resolve,
/// codeAction/resolve). Clients round-trip an opaque <c>data</c> payload; the heavy work
/// (documentation text, workspace-edit computation) runs only for the item the user actually
/// selects instead of for every candidate up front.
/// </summary>
internal sealed class LspResolveCache
{
    private readonly object _lock = new();

    // ---- Completion: only the latest list matters (a new request invalidates the old menu).
    private long _completionCacheId;
    private Document? _completionDocument;
    private IReadOnlyList<RoslynCompletionItem>? _completionItems;

    public long StoreCompletions(Document document, IReadOnlyList<RoslynCompletionItem> items)
    {
        lock (_lock)
        {
            _completionDocument = document;
            _completionItems = items;
            return ++_completionCacheId;
        }
    }

    public (Document Document, RoslynCompletionItem Item)? GetCompletion(long cacheId, int index)
    {
        lock (_lock)
        {
            if (cacheId != _completionCacheId || _completionDocument is null || _completionItems is null)
                return null;
            if (index < 0 || index >= _completionItems.Count)
                return null;
            return (_completionDocument, _completionItems[index]);
        }
    }

    // ---- Code actions: monotonic ids, bounded (the client may resolve an action from a menu
    // that is still open while a newer request has already fired).
    private const int MaxCachedActions = 200;
    private long _nextActionId;
    private readonly Queue<long> _actionOrder = new();
    private readonly Dictionary<long, (RoslynCodeAction Action, Solution OldSolution)> _actions = new();

    public long StoreAction(RoslynCodeAction action, Solution oldSolution)
    {
        lock (_lock)
        {
            long id = ++_nextActionId;
            _actions[id] = (action, oldSolution);
            _actionOrder.Enqueue(id);
            while (_actionOrder.Count > MaxCachedActions)
                _actions.Remove(_actionOrder.Dequeue());
            return id;
        }
    }

    public (RoslynCodeAction Action, Solution OldSolution)? GetAction(long id)
    {
        lock (_lock)
        {
            return _actions.TryGetValue(id, out var entry) ? entry : null;
        }
    }
}

using Microsoft.CodeAnalysis;
using RoslynMCP.Services.Memory;
using RoslynCodeAction = Microsoft.CodeAnalysis.CodeActions.CodeAction;
using RoslynCompletionItem = Microsoft.CodeAnalysis.Completion.CompletionItem;

namespace RoslynMCP.Lsp;

/// <summary>Short-lived request groups; a visible menu is never evicted one leaf at a time.</summary>
internal sealed class LspResolveCache : IMemoryCache, IDisposable
{
    private readonly object _lock = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _lifetime;
    private bool _disposed;
    private long _nextId, _completionCacheId, _completionAt, _generation;
    public long Generation { get { lock (_lock) return _generation; } }
    private Document? _completionDocument;
    private IReadOnlyList<RoslynCompletionItem>? _completionItems;
    private readonly Dictionary<long, ActionGroup> _groups = new();

    private sealed class ActionGroup(long id, Solution solution, long timestamp)
    {
        public readonly long Id = id;
        public readonly Solution Solution = solution;
        public long Timestamp = timestamp;
        public bool Complete;
        public readonly Dictionary<long, RoslynCodeAction> Actions = new();
    }

    public LspResolveCache(TimeProvider? time = null, TimeSpan? lifetime = null)
    {
        _time = time ?? TimeProvider.System;
        _lifetime = lifetime ?? TimeSpan.FromMinutes(2);
        MemoryCacheRegistry.Register(this);
    }

    public sealed class RequestGroup(LspResolveCache owner, long id) : IDisposable
    {
        public long Id => id;
        public void Dispose() => owner.Complete(id);
    }

    public RequestGroup BeginActions(Document document)
    {
        lock (_lock)
        {
            long id = ++_nextId;
            if (!_disposed) _groups[id] = new(id, document.Project.Solution, _time.GetTimestamp());
            return new(this, id);
        }
    }

    private void Complete(long id)
    {
        lock (_lock)
        {
            if (_groups.TryGetValue(id, out var group))
            {
                if (!group.Complete) group.Timestamp = _time.GetTimestamp();
                group.Complete = true;
                if (group.Actions.Count == 0) _groups.Remove(id);
            }
            TrimLocked();
        }
    }

    public long StoreAction(RoslynCodeAction action, Solution oldSolution, long? groupId = null)
    {
        lock (_lock)
        {
            long id = ++_nextId;
            if (_disposed) return id;
            if (groupId is null)
            {
                // Compatibility for direct callers: one implicit group per solution snapshot.
                groupId = _groups.Values.LastOrDefault(g => ReferenceEquals(g.Solution, oldSolution))?.Id;
                if (groupId is null)
                {
                    groupId = id;
                    _groups[id] = new(id, oldSolution, _time.GetTimestamp()) { Complete = true };
                }
            }
            if (_groups.TryGetValue(groupId.Value, out var group)) group.Actions[id] = action;
            TrimLocked();
            return id;
        }
    }

    public (RoslynCodeAction Action, Solution OldSolution)? GetAction(long id)
    {
        lock (_lock)
        {
            TrimLocked();
            foreach (var group in _groups.Values)
                if (group.Actions.TryGetValue(id, out var action)) return (action, group.Solution);
            return null;
        }
    }

    public long StoreCompletions(Document document, IReadOnlyList<RoslynCompletionItem> items, long? expectedGeneration = null)
    {
        lock (_lock)
        {
            if (expectedGeneration is { } expected && expected != _generation) return -1;
            if (!_disposed) { _completionDocument = document; _completionItems = items; _completionAt = _time.GetTimestamp(); }
            return ++_completionCacheId;
        }
    }

    public (Document Document, RoslynCompletionItem Item)? GetCompletion(long cacheId, int index)
    {
        lock (_lock)
        {
            TrimLocked();
            if (cacheId != _completionCacheId || _completionDocument is null || _completionItems is null
                || index < 0 || index >= _completionItems.Count) return null;
            return (_completionDocument, _completionItems[index]);
        }
    }

    private void TrimLocked()
    {
        var now = _time.GetTimestamp();
        foreach (var group in _groups.Values.ToArray())
            if (group.Complete && _time.GetElapsedTime(group.Timestamp, now) >= _lifetime) _groups.Remove(group.Id);
        foreach (var group in _groups.Values.Where(g => g.Complete).OrderByDescending(g => g.Id).Skip(2).ToArray())
            _groups.Remove(group.Id);
        if (_completionDocument is not null && _time.GetElapsedTime(_completionAt, now) >= _lifetime)
            ClearCompletion();
    }

    private void ClearCompletion() { _completionDocument = null; _completionItems = null; _completionCacheId++; }
    public void Trim() { lock (_lock) TrimLocked(); }
    public void DocumentChanged(string path)
    {
        lock (_lock)
        {
            _generation++;
            // A refactoring may reach another file, so any buffer change invalidates action groups.
            _groups.Clear();
            // This also covers synthetic projection paths and cross-file import context.
            ClearCompletion();
        }
    }
    public void WorkspaceChanged() { lock (_lock) { _generation++; _groups.Clear(); ClearCompletion(); } }
    public void Dispose() { lock (_lock) { _disposed = true; _generation++; _groups.Clear(); ClearCompletion(); } }
    public CacheMemoryInfo Inspect()
    {
        lock (_lock) return new("lsp.resolve", _groups.Sum(g => g.Value.Actions.Count) + (_completionItems?.Count ?? 0),
            _groups.Count, _groups.Count + (_completionDocument is null ? 0 : 1));
    }

    public static Exception Expired(string kind) => new StreamJsonRpc.LocalRpcException(
        $"The {kind} has expired or the document changed. Reopen the menu to request it again.") { ErrorCode = -32801 };
}

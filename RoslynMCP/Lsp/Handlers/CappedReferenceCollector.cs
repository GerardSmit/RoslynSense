using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Shared.Utilities;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>
/// Collects reference locations until a cap is exceeded, then cancels the search.
/// </summary>
/// <remarks>
/// Modelled on Roslyn's <c>CodeLensFindReferencesProgress</c>. Cancellation is the only way to
/// stop the reference engine mid-search, so the cap owns a token linked to the caller's and the
/// caller tells "the cap stopped it" from "the caller stopped it" by <see cref="CapReached"/>.
/// Definitions are ignored: the lens that owns this counts references excluding the declaration.
/// </remarks>
internal sealed class CappedReferenceCollector : IStreamingFindReferencesProgress, IDisposable
{
    private readonly CancellationTokenSource _cancellation;
    private readonly object _gate = new();
    private readonly List<Location> _locations = [];
    private readonly int _cap;

    public CappedReferenceCollector(int cap, CancellationToken ct)
    {
        _cap = cap;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
    }

    /// <summary>The token the search must be given, so the cap can stop it.</summary>
    public CancellationToken CancellationToken => _cancellation.Token;

    /// <summary>
    /// Strictly greater than the cap: a symbol with exactly <c>cap</c> references has an exact
    /// count, and only one more makes the number a lower bound.
    /// </summary>
    public bool CapReached
    {
        get { lock (_gate) return _locations.Count > _cap; }
    }

    public ImmutableArray<Location> Locations
    {
        get { lock (_gate) return [.. _locations]; }
    }

    public IStreamingProgressTracker ProgressTracker { get; } =
        NoOpStreamingFindReferencesProgress.Instance.ProgressTracker;

    public ValueTask OnStartedAsync(CancellationToken cancellationToken) => default;

    public ValueTask OnCompletedAsync(CancellationToken cancellationToken) => default;

    public ValueTask OnDefinitionFoundAsync(SymbolGroup group, CancellationToken cancellationToken) => default;

    public ValueTask OnReferencesFoundAsync(
        ImmutableArray<(SymbolGroup group, ISymbol symbol, ReferenceLocation location)> references,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            foreach (var (_, _, location) in references)
                _locations.Add(location.Location);
        }

        if (CapReached)
            _cancellation.Cancel();

        return default;
    }

    public void Dispose() => _cancellation.Dispose();
}

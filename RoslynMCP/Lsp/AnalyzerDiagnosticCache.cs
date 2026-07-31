using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMCP.Config;
using RoslynMCP.Services;

namespace RoslynMCP.Lsp;

/// <summary>
/// Caches analyzer diagnostics per document version so the expensive pass runs once per edit
/// instead of once per request. The key mirrors the pull-diagnostics resultId scheme
/// (text checksum + dependent semantic version), so a cache hit and an "unchanged" pull
/// report agree on what "same world" means.
/// </summary>
internal static class AnalyzerDiagnosticCache
{
    private const int MaxEntries = 64;

    private static readonly ConcurrentDictionary<DocumentId, Entry> s_entries = new();
    // Lazy, not Task: ConcurrentDictionary may invoke a GetOrAdd factory more than once under
    // contention, and an analyzer pass is far too expensive to run twice for one version.
    private static readonly ConcurrentDictionary<(DocumentId, string), Lazy<Task<ImmutableArray<Diagnostic>>>> s_inFlight = new();
    private static long s_clock;

    private sealed record Entry(string Version, ImmutableArray<Diagnostic> Diagnostics, long Stamp);

    /// <summary>The cache key for a document, or null when it cannot be versioned.</summary>
    public static async Task<string?> GetVersionAsync(Document document, CancellationToken ct)
    {
        try
        {
            var text = await document.GetTextAsync(ct);
            var semanticVersion = await document.Project.GetDependentSemanticVersionAsync(ct);
            return $"{Convert.ToHexString(text.GetChecksum().AsSpan())}:{semanticVersion}";
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>Whether this exact document version has already been analyzed. An analyzed
    /// document with no findings is a real answer, not a miss — distinguishing the two is what
    /// keeps the pull path from re-queueing a background pass on every request.</summary>
    public static bool IsComputed(Document document, string? version) =>
        version is not null && s_entries.TryGetValue(document.Id, out var entry) && entry.Version == version;

    /// <summary>Cached diagnostics for this exact document version, without computing.</summary>
    public static ImmutableArray<Diagnostic> TryGet(Document document, string? version)
    {
        if (version is null || !s_entries.TryGetValue(document.Id, out var entry) || entry.Version != version)
            return ImmutableArray<Diagnostic>.Empty;

        s_entries[document.Id] = entry with { Stamp = Interlocked.Increment(ref s_clock) };
        return entry.Diagnostics;
    }

    /// <summary>Cached diagnostics, computing and storing them on a miss.</summary>
    public static async Task<ImmutableArray<Diagnostic>> GetOrComputeAsync(
        Document document, CancellationToken ct)
    {
        if (!LspFeatureOptions.AnalyzerDiagnostics)
            return ImmutableArray<Diagnostic>.Empty;

        var version = await GetVersionAsync(document, ct);
        if (IsComputed(document, version))
            return TryGet(document, version);

        if (version is null)
            return await AnalyzerService.RunDocumentAnalyzersAsync(document, ct);

        // A pull-diagnostics client re-requests on every keystroke; without this guard each
        // request would start its own analyzer pass over the same unchanged document.
        var key = (document.Id, version);
        var work = s_inFlight.GetOrAdd(key,
            _ => new Lazy<Task<ImmutableArray<Diagnostic>>>(() => ComputeAsync(document, version, ct)));
        try
        {
            return await work.Value;
        }
        finally
        {
            s_inFlight.TryRemove(key, out _);
        }
    }

    private static async Task<ImmutableArray<Diagnostic>> ComputeAsync(
        Document document, string version, CancellationToken ct)
    {
        var diagnostics = await AnalyzerService.RunDocumentAnalyzersAsync(document, ct);
        s_entries[document.Id] = new Entry(version, diagnostics, Interlocked.Increment(ref s_clock));
        Trim();
        return diagnostics;
    }

    public static void Evict(DocumentId documentId) => s_entries.TryRemove(documentId, out _);

    /// <summary>Drops everything — used when analyzer configuration changes (.editorconfig edits).</summary>
    public static void Clear()
    {
        s_entries.Clear();
        s_inFlight.Clear();
    }

    private static void Trim()
    {
        if (s_entries.Count <= MaxEntries)
            return;

        foreach (var stale in s_entries.OrderBy(e => e.Value.Stamp).Take(s_entries.Count - MaxEntries).ToList())
            s_entries.TryRemove(stale.Key, out _);
    }
}

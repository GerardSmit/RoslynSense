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
    /// <summary>
    /// Documents to keep analyzer results for.
    /// </summary>
    /// <remarks>
    /// This was 64, which is a plausible number of open tabs and a poor number of analyzed files:
    /// the workspace sweep reads this cache without computing, so every document past the ceiling
    /// reported its compiler-only subset in the Problems panel and re-ran its pass the next time it
    /// was pulled. The entries are diagnostics for one file — small, and bounded by how many files
    /// have actually been analyzed — so the ceiling can sit well above any real working set and
    /// stay a runaway guard rather than a working limit.
    /// </remarks>
    private const int MaxEntries = 2048;

    private static readonly ConcurrentDictionary<DocumentId, Entry> s_entries = new();
    // Lazy, not Task: ConcurrentDictionary may invoke a GetOrAdd factory more than once under
    // contention, and an analyzer pass is far too expensive to run twice for one version.
    private static readonly ConcurrentDictionary<(DocumentId, string), Lazy<Task<ImmutableArray<Diagnostic>>>> s_inFlight = new();

    /// <summary>
    /// The most recent version anyone has asked to have analysed, per document.
    /// </summary>
    /// <remarks>
    /// A finishing pass has to know whether it has been overtaken, and neither of the other two
    /// orderings can tell it: version strings are checksums with no order, and write generations
    /// order completion rather than recency — so of two passes started together, whichever finished
    /// first won, which is as likely to be the older one. This records what was asked for, in the
    /// order it was asked.
    /// </remarks>
    private static readonly ConcurrentDictionary<DocumentId, string> s_latestRequested = new();
    private static long s_clock;
    private static long s_writeClock;

    /// <summary>
    /// <paramref name="Stamp"/> orders <em>use</em> and is what <see cref="Trim"/> evicts by;
    /// <paramref name="Written"/> orders <em>writes</em> and is what decides whether a finishing
    /// analyzer pass has been overtaken.
    /// </summary>
    /// <remarks>
    /// Two counters because one cannot mean both. While the LRU touch and the write generation
    /// were the same field, a concurrent read of the previous entry — the sweep looking up a
    /// document while a pass for the next version was running — advanced it, and the finishing pass
    /// could not tell that from someone having stored something newer. It stepped aside and threw
    /// its own strictly-newer result away, which is the squiggle flicker this cache exists to stop.
    /// </remarks>
    private sealed record Entry(
        string Version, ImmutableArray<Diagnostic> Diagnostics, long Stamp, long Written);

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

        // Conditional: a plain assignment is a read-modify-write, and the sweep runs this in
        // parallel with the pull path and with a completing analyzer pass. Losing that race put a
        // superseded entry back over a fresh one.
        s_entries.TryUpdate(document.Id, entry with { Stamp = Interlocked.Increment(ref s_clock) }, entry);
        return entry.Diagnostics;
    }

    /// <summary>
    /// Whatever this document's last analysis said, whichever version produced it — for comparing
    /// a fresh result against the one it replaces.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="TryGetAnyVersion"/> on purpose. That one answers "what is safe to
    /// show", and needs the current version to prove the spans still line up; this one answers
    /// "what did we last say", which has no version to check against. Overloading the first for the
    /// second by passing a null version silently returned nothing every time, which turned a
    /// did-the-answer-change comparison into an unconditional yes.
    /// </remarks>
    public static ImmutableArray<Diagnostic> TryGetPrevious(Document document) =>
        s_entries.TryGetValue(document.Id, out var entry)
            ? entry.Diagnostics
            : ImmutableArray<Diagnostic>.Empty;

    public static ImmutableArray<Diagnostic> TryGetAnyVersion(Document document, string? version)
    {
        if (version is null || !s_entries.TryGetValue(document.Id, out var entry))
            return ImmutableArray<Diagnostic>.Empty;

        // Only when the document's own text is the text these were computed from. The key is
        // "textChecksum:dependentSemanticVersion", so an equal checksum with a different semantic
        // version means something elsewhere moved and this file did not — the findings still
        // describe the code, and every span still lands where it did.
        //
        // If the text itself changed, they do not: the spans were resolved against the previous
        // syntax tree, so serving them would draw squiggles on the wrong lines. That case gets
        // nothing and waits for the recompute.
        return SameText(entry.Version, version) ? entry.Diagnostics : ImmutableArray<Diagnostic>.Empty;
    }

    /// <summary>
    /// Whether two analyzer results say the same thing, so nothing need be republished.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>SequenceEqual</c>. A <see cref="Diagnostic"/> compares its
    /// <see cref="Location"/>, and a source location compares its <see cref="SyntaxTree"/> by
    /// reference — so for the file being edited, whose tree is new every keystroke, no two results
    /// are ever equal however identical their findings. Comparing what the user actually sees, and
    /// in a fixed order because the analyzer driver is concurrent and its output order is not
    /// promised.
    /// </remarks>
    public static bool SameFindings(ImmutableArray<Diagnostic> before, ImmutableArray<Diagnostic> after)
    {
        if (before.Length != after.Length)
            return false;

        static IEnumerable<(string, Microsoft.CodeAnalysis.Text.TextSpan, DiagnosticSeverity, string)> Key(
            ImmutableArray<Diagnostic> items) =>
            items
                .Select(d => (d.Id, d.Location.SourceSpan, d.Severity, d.GetMessage()))
                .OrderBy(d => d.SourceSpan.Start)
                .ThenBy(d => d.Id, StringComparer.Ordinal);

        return Key(before).SequenceEqual(Key(after));
    }

    private static bool SameText(string cachedVersion, string version)
    {
        int cached = cachedVersion.IndexOf(':');
        int current = version.IndexOf(':');
        if (cached < 0 || current < 0 || cached != current)
            return false;

        return cachedVersion.AsSpan(0, cached).SequenceEqual(version.AsSpan(0, current));
    }

    /// <summary>
    /// Whether a computed result was actually written to the cache.
    /// </summary>
    /// <remarks>
    /// A run that timed out, or one overtaken by a newer version, stores nothing — so the result id
    /// cannot move, and asking the editor to re-pull can only produce the same answer. Reported so
    /// the caller can decline to ask. Deliberately not a comparison of the findings: the pull path
    /// owes its follow-up whenever the analysers landed, whether or not they changed anything.
    /// </remarks>
    public static bool LastComputeStored(Document document, string? version) =>
        version is not null && IsComputed(document, version);

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
        s_latestRequested[document.Id] = version;

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
        // The stamp of whatever is cached before this run starts. Stamps come from a monotonic
        // counter, so they order writes; the version string does not — it is a checksum and an
        // opaque semantic stamp, and comparing versions for inequality cannot tell "someone stored
        // something newer" from "the entry holds the previous version". Treating the second as the
        // first rejected almost every legitimate result, freezing each document at whatever it was
        // analysed as first and making every later pass run and be thrown away.
        long observed = s_entries.TryGetValue(document.Id, out var before) ? before.Written : long.MinValue;

        var run = await AnalyzerService.RunDocumentAnalyzersWithStatusAsync(document, ct);

        // A run that gave up is not a result. Storing it would say "this version is analysed and
        // clean", so nothing would ever look at the file again.
        if (run.Failed)
            return run.Diagnostics;

        // Against what is cached, not against the snapshot we were handed. A Document is immutable,
        // so re-deriving its version here would always agree with itself — the check has to ask
        // whether someone else has since stored a newer answer. A pass queued while the file was at
        // V1 can finish long after the editor moved it to V2, because it waits for an analyzer
        // slot, and writing V1 back over V2 makes the cache miss again and the squiggles blink out.
        var replacement = new Entry(
            version,
            run.Diagnostics,
            Interlocked.Increment(ref s_clock),
            Interlocked.Increment(ref s_writeClock));

        while (true)
        {
            if (!s_entries.TryGetValue(document.Id, out var existing))
            {
                if (s_entries.TryAdd(document.Id, replacement))
                    break;
                continue;
            }

            // Only step aside for a write that landed after this run began. That is the case the
            // guard is for: a pass queued while the file was at V1 can finish long after the editor
            // moved it to V2, because it waits for an analyzer slot, and V1 must not overwrite V2.
            if (s_latestRequested.TryGetValue(document.Id, out var latest)
                && latest != version
                && existing.Written > observed)
            {
                return run.Diagnostics;
            }

            if (s_entries.TryUpdate(document.Id, replacement, existing))
                break;
        }

        Trim();
        return run.Diagnostics;
    }

    public static void Evict(DocumentId documentId)
    {
        s_entries.TryRemove(documentId, out _);
        s_latestRequested.TryRemove(documentId, out _);
    }

    /// <summary>Drops everything — used when analyzer configuration changes (.editorconfig edits).</summary>
    public static void Clear()
    {
        s_entries.Clear();
        s_inFlight.Clear();
        s_latestRequested.Clear();
    }

    private static void Trim()
    {
        if (s_entries.Count <= MaxEntries)
            return;

        foreach (var stale in s_entries.OrderBy(e => e.Value.Stamp).Take(s_entries.Count - MaxEntries).ToList())
            s_entries.TryRemove(stale.Key, out _);
    }
}

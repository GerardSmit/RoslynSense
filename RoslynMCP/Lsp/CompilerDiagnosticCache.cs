using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMCP.Lsp.Handlers;

namespace RoslynMCP.Lsp;

/// <summary>
/// Caches compiler diagnostics — and the embedded-language findings computed alongside them — per
/// document version, so one typing pause binds the file once instead of once per requester.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SemanticModel.GetDiagnostics(Microsoft.CodeAnalysis.Text.TextSpan?, CancellationToken)"/>
/// re-binds every method body on every call and memoizes nothing. The same text was bound up to
/// three times per pause: the fast push phase, the analyzer push phase behind it, and the pull
/// path — plus the re-pull the background analyzer pass asks for, where only the result-id marker
/// moved from <c>c</c> to <c>a</c> and the diagnostics could not possibly have changed.
/// </para>
/// <para>
/// The key is <see cref="AnalyzerDiagnosticCache.GetVersionAsync"/>'s, deliberately identical:
/// text checksum plus dependent semantic version is exactly the condition under which binding can
/// produce a different answer, and it is already the rule the pull-diagnostics resultId scheme
/// relies on. So the cache needs no invalidation of its own — the key <em>is</em> the invalidation
/// — and a null version (a document that cannot be versioned) simply bypasses it.
/// </para>
/// <para>
/// Embedded-language results live in the same entry because they are recomputed on the same three
/// paths, from the same document, and a walk over every token of the file is not cheaper than the
/// bind it accompanies.
/// </para>
/// </remarks>
internal static class CompilerDiagnosticCache
{
    /// <summary>
    /// Documents to keep compiler results for. Matches
    /// <see cref="AnalyzerDiagnosticCache"/>'s ceiling for the same reason: an entry is the
    /// diagnostics of one file, bounded by how many files have actually been asked about, so the
    /// limit is a runaway guard rather than a working-set limit — the workspace sweep reads far
    /// more documents than a client has tabs open.
    /// </summary>
    private const int MaxEntries = 2048;

    private static readonly ConcurrentDictionary<DocumentId, Entry> s_entries = new();

    // Lazy, not Task: ConcurrentDictionary may invoke a GetOrAdd factory more than once under
    // contention, and the whole point here is that one version is bound once. The push phases and
    // the pull path routinely arrive at the same document within milliseconds of each other.
    private static readonly ConcurrentDictionary<(DocumentId, string), Lazy<Task<Result>>> s_inFlight = new();

    private static long s_clock;
    private static long s_computations;
    private static long s_spanBinds;

    /// <summary><paramref name="Stamp"/> orders use and is what <see cref="Trim"/> evicts by.</summary>
    /// <remarks>
    /// No write-generation guard, unlike <see cref="AnalyzerDiagnosticCache"/>. That guard exists
    /// because an analyzer pass runs in the background, can finish long after the version it was
    /// started for is gone, and its results are served for neighbouring versions. This cache is
    /// computed inside the request that needs it and is only ever read on an exact version match,
    /// so a write that loses a race costs one recompute and can never put a stale span on screen.
    /// </remarks>
    private sealed record Entry(string Version, Result Result, long Stamp);

    /// <summary>What one bind of a document produced.</summary>
    internal sealed record Result(
        ImmutableArray<Diagnostic> Compiler, IReadOnlyList<Protocol.Diagnostic> Embedded)
    {
        public static readonly Result Empty = new(ImmutableArray<Diagnostic>.Empty, []);
    }

    /// <summary>
    /// Compiler and embedded diagnostics for this document, binding only on a miss.
    /// </summary>
    /// <param name="version">
    /// The caller's already-derived version, when it has one. The pull path computes it to build
    /// the resultId; re-deriving it here would checksum the text a second time per request.
    /// </param>
    public static async Task<Result> GetOrComputeAsync(
        Document document, CancellationToken ct, string? version = null)
    {
        version ??= await AnalyzerDiagnosticCache.GetVersionAsync(document, ct);

        if (version is null)
            return await ComputeAsync(document, version: null, ct);

        if (s_entries.TryGetValue(document.Id, out var entry) && entry.Version == version)
        {
            // Conditional: a plain assignment is a read-modify-write, and the sweep runs in
            // parallel with the pull path.
            s_entries.TryUpdate(document.Id, entry with { Stamp = Interlocked.Increment(ref s_clock) }, entry);
            return entry.Result;
        }

        var key = (document.Id, version);
        var work = s_inFlight.GetOrAdd(key,
            _ => new Lazy<Task<Result>>(() => ComputeAndStoreAsync(document, version, ct)));
        try
        {
            return await work.Value;
        }
        finally
        {
            s_inFlight.TryRemove(key, out _);
        }
    }

    private static async Task<Result> ComputeAndStoreAsync(
        Document document, string version, CancellationToken ct)
    {
        var result = await ComputeAsync(document, version, ct);
        s_entries[document.Id] = new Entry(version, result, Interlocked.Increment(ref s_clock));
        Trim();
        return result;
    }

    private static async Task<Result> ComputeAsync(Document document, string? version, CancellationToken ct)
    {
        Interlocked.Increment(ref s_computations);

        var model = await document.GetSemanticModelAsync(ct);
        return new Result(
            model is null ? ImmutableArray<Diagnostic>.Empty : await BindAsync(document, model, version, ct),
            await DiagnosticsHandler.EmbeddedDiagnosticsAsync(document, ct));
    }

    /// <summary>
    /// One member's widened span when a single keystroke is all that separates this version from
    /// the last one bound, the whole file otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is where the incremental path pays: <c>GetDiagnostics()</c> with no span binds every
    /// method body in the file, and with one it binds the bodies that span touches. The previous
    /// entry supplies everything outside it, mapped over the edit.
    /// </para>
    /// <para>
    /// Everything the compiler reports is span-limited here, unlike the analyzer half where only
    /// some analyzers are — hence the unconditional <c>true</c>. The exception the splice already
    /// knows about is CS8019: a using directive is never examined under a span, so its greying can
    /// only ever come forward from the last whole-file bind.
    /// </para>
    /// </remarks>
    private static async Task<ImmutableArray<Diagnostic>> BindAsync(
        Document document, SemanticModel model, string? version, CancellationToken ct)
    {
        if (version is null)
            return model.GetDiagnostics(cancellationToken: ct);

        MemberEditAnalysis.Observe(document, version);

        if (await MemberEditAnalysis.TryComputeAsync(document, version, ct) is not { } edit
            || !s_entries.TryGetValue(document.Id, out var prior)
            || prior.Version != edit.BaseVersion)
        {
            return model.GetDiagnostics(cancellationToken: ct);
        }

        Interlocked.Increment(ref s_spanBinds);

        return MemberEditAnalysis.Splice(
            prior.Result.Compiler,
            model.GetDiagnostics(edit.CompilerSpan, ct),
            edit,
            edit.CompilerSpan,
            static _ => true);
    }

    public static void Evict(DocumentId documentId) => s_entries.TryRemove(documentId, out _);

    /// <summary>Drops everything — .editorconfig can change a compiler diagnostic's severity, so
    /// the same bind of the same text is entitled to a different answer afterwards.</summary>
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
        {
            s_entries.TryRemove(stale.Key, out _);

            // The member-edit record is written before either cache has an entry, and is only
            // useful while one of them does. Dropped here as well as from the analyzer cache's
            // trim, because with analyzer diagnostics switched off that trim never runs.
            MemberEditAnalysis.Forget(stale.Key);
        }
    }

    // ---- Test hooks (exposed via InternalsVisibleTo) ----

    /// <summary>Binds performed, cache misses included and hits excluded — the quantity this cache
    /// exists to hold down, and the only way to observe a hit from outside: the diagnostics of a
    /// hit and of a miss are equal by construction.</summary>
    internal static long Computations => Interlocked.Read(ref s_computations);

    /// <summary>Of those binds, the ones restricted to a single edited member's span.</summary>
    internal static long SpanBinds => Interlocked.Read(ref s_spanBinds);

    internal static void ResetComputationCounter()
    {
        Interlocked.Exchange(ref s_computations, 0);
        Interlocked.Exchange(ref s_spanBinds, 0);
    }
}

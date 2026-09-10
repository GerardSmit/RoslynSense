using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.LanguageService;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.SolutionCrawler;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Config;
using RoslynMCP.Services;

namespace RoslynMCP.Lsp;

/// <summary>
/// The typing-loop shortcut both diagnostic caches share: when one keystroke changed the inside of
/// exactly one method-level member, re-analyse that member's span and splice the findings into what
/// the previous version said, instead of re-analysing the whole file.
/// </summary>
/// <remarks>
/// <para>
/// This is Roslyn's <c>IncrementalMemberEditAnalyzer</c>. The diff comes from the same place —
/// <see cref="IDocumentDifferenceService.GetChangedMemberAsync"/>, which returns null for anything
/// that is not a single body-level edit, signature changes included — and the compiler's span is
/// widened the same way, to the containing method-level declarations of both ends.
/// </para>
/// <para>
/// The previous document is held weakly. A <see cref="Document"/> roots its whole
/// <see cref="Solution"/> and every compilation hanging off it, and the point of this cache is to
/// hold less work, not more memory; if the edit before last has already been collected the pass
/// simply falls back to the whole file.
/// </para>
/// </remarks>
internal static class MemberEditAnalysis
{
    /// <summary>Off switch, for tests that need the whole-file answer to compare against.</summary>
    internal static bool Enabled { get; set; } = true;

    /// <summary>Splices performed. The only outward difference between an incremental pass and a
    /// full one is meant to be its cost, so nothing else can observe which ran.</summary>
    internal static long Splices => Interlocked.Read(ref s_splices);

    internal static void ResetCounters() => Interlocked.Exchange(ref s_splices, 0);

    private static long s_splices;

    private sealed record Seen(string Version, WeakReference<Document> Document);

    private sealed record Slot(Seen? Previous, Seen Current);

    private static readonly ConcurrentDictionary<DocumentId, Slot> s_seen = new();

    /// <summary>
    /// Records the version a pass is about to compute, rotating the one before it into the
    /// previous slot.
    /// </summary>
    /// <remarks>
    /// Two slots rather than one because two caches compute the same version one after the other:
    /// the compiler pass arrives at V2 first and would, with a single slot, overwrite the V1
    /// document that the analyzer pass behind it still needs to diff against. Rotation is keyed on
    /// the version, so the second caller of a version changes nothing.
    /// </remarks>
    public static void Observe(Document document, string? version)
    {
        if (version is null)
            return;

        s_seen.AddOrUpdate(
            document.Id,
            static (_, state) => new Slot(null, new Seen(state.Version, new WeakReference<Document>(state.Document))),
            static (_, slot, state) => slot.Current.Version == state.Version
                ? slot
                : new Slot(slot.Current, new Seen(state.Version, new WeakReference<Document>(state.Document))),
            (Version: version, Document: document));
    }

    public static void Forget(DocumentId documentId) => s_seen.TryRemove(documentId, out _);

    public static void Clear()
    {
        s_seen.Clear();
        ResetCounters();
    }

    /// <summary>What one keystroke changed, when it changed one member's body and nothing else.</summary>
    /// <param name="BaseVersion">The version the previous whole-file result was computed for — a
    /// caller may only splice into a cache entry stamped with exactly this.</param>
    /// <param name="MemberSpan">The changed member's full span, in the new document's coordinates.</param>
    /// <param name="CompilerSpan">The same, widened the way Roslyn widens it for the compiler.</param>
    /// <param name="Change">The edit, in the <em>previous</em> document's coordinates — what prior
    /// spans have to be mapped through.</param>
    internal sealed record MemberEdit(
        string BaseVersion,
        SyntaxTree Tree,
        TextSpan MemberSpan,
        TextSpan CompilerSpan,
        TextChangeRange Change);

    /// <summary>
    /// The edit to analyse incrementally, or null when this pass has to look at the whole file.
    /// </summary>
    /// <remarks>
    /// Every condition here is a way for the splice to be unsound rather than merely unhelpful, so
    /// each one falls back rather than approximating:
    /// the previous document is gone; the semantic half of the version moved, which is exactly what
    /// a signature or any other top-level change does; the text moved in more than one place;
    /// the differ says no single member owns the change; or the edit reaches outside the member it
    /// was attributed to.
    /// </remarks>
    public static async Task<MemberEdit?> TryComputeAsync(
        Document document, string version, CancellationToken ct)
    {
        if (!Enabled)
            return null;

        if (!s_seen.TryGetValue(document.Id, out var slot)
            || slot.Current.Version != version
            || slot.Previous is not { } previous
            || !previous.Document.TryGetTarget(out var old))
            return null;

        // "textChecksum:dependentSemanticVersion". Requiring the semantic half to stand still is
        // both the same-world guard and the signature-edit fallback: a changed declaration moves
        // the project's dependent semantic version, and every other file's cached result with it.
        if (!SameSemantics(previous.Version, version))
            return null;

        try
        {
            var oldText = await old.GetTextAsync(ct);
            var newText = await document.GetTextAsync(ct);

            var ranges = newText.GetChangeRanges(oldText);
            if (ranges.Count != 1)
                return null;
            var change = ranges[0];

            if (document.GetLanguageService<IDocumentDifferenceService>() is not { } differ)
                return null;

            var member = await differ.GetChangedMemberAsync(old, document, ct);
            if (member is null)
                return null;

            var root = await document.GetSyntaxRootAsync(ct);
            if (root is null || member.SyntaxTree != root.SyntaxTree)
                return null;

            var memberSpan = member.FullSpan;

            // The edit has to live inside the member the differ named, or prior findings between
            // the two would be mapped through a delta that does not describe them.
            if (!memberSpan.Contains(new TextSpan(change.Span.Start, change.NewLength)))
                return null;

            return new MemberEdit(
                previous.Version,
                root.SyntaxTree,
                memberSpan,
                AdjustForCompiler(document, root, memberSpan),
                change);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>
    /// The compiler reports at locations outside the span it was given — an unused parameter is
    /// flagged at the declaration, not in the body — so its span is grown to the containing
    /// method-level declarations of both ends, exactly as
    /// <c>DocumentAnalysisExecutor.GetAdjustedSpanForCompilerAnalyzerAsync</c> does.
    /// </summary>
    private static TextSpan AdjustForCompiler(Document document, SyntaxNode root, TextSpan span)
    {
        if (document.GetLanguageService<ISyntaxFactsService>() is not { } facts)
            return span;

        var startNode = facts.GetContainingMemberDeclaration(root, span.Start, useFullSpan: true);
        var endNode = facts.GetContainingMemberDeclaration(root, span.End, useFullSpan: true);
        if (startNode is null || endNode is null)
            return span;

        if (startNode == endNode)
            return facts.IsMethodLevelMember(startNode) ? startNode.FullSpan : span;

        var startSpan = facts.IsMethodLevelMember(startNode) ? startNode.FullSpan : span;
        var endSpan = facts.IsMethodLevelMember(endNode) ? endNode.FullSpan : span;
        return TextSpan.FromBounds(
            Math.Min(startSpan.Start, endSpan.Start), Math.Max(startSpan.End, endSpan.End));
    }

    private static bool SameSemantics(string a, string b)
    {
        int i = a.IndexOf(':');
        int j = b.IndexOf(':');
        return i >= 0 && j >= 0 && a.AsSpan(i).SequenceEqual(b.AsSpan(j));
    }

    /// <summary>
    /// Where a span from the previous version lands in this one, or null when the edit went
    /// through it and it no longer describes anything.
    /// </summary>
    public static TextSpan? Map(TextSpan span, TextChangeRange change)
    {
        if (span.End <= change.Span.Start)
            return span;
        if (span.Start >= change.Span.End)
            return new TextSpan(span.Start + change.NewLength - change.Span.Length, span.Length);
        return null;
    }

    /// <summary>
    /// The previous whole-file result, brought forward over the edit, with everything the fresh
    /// pass is authoritative about removed and the fresh findings put in its place.
    /// </summary>
    /// <param name="analyzedSpan">What the fresh pass actually looked at.</param>
    /// <param name="spanLimited">Whether the fresh pass only speaks for <paramref name="analyzedSpan"/>
    /// on this id. False means it examined the whole file and its answer is complete.</param>
    public static ImmutableArray<Diagnostic> Splice(
        ImmutableArray<Diagnostic> previous,
        ImmutableArray<Diagnostic> fresh,
        MemberEdit edit,
        TextSpan analyzedSpan,
        Func<string, bool> spanLimited)
    {
        Interlocked.Increment(ref s_splices);

        var seen = new HashSet<(string, TextSpan)>();
        var result = ImmutableArray.CreateBuilder<Diagnostic>(fresh.Length + previous.Length);

        foreach (var diagnostic in fresh)
        {
            if (seen.Add((diagnostic.Id, diagnostic.Location.SourceSpan)))
                result.Add(diagnostic);
        }

        foreach (var diagnostic in previous)
        {
            if (!diagnostic.Location.IsInSource)
                continue;
            if (Map(diagnostic.Location.SourceSpan, edit.Change) is not { } mapped)
                continue;

            // Never reported under a span at all — the compiler does not look at using directives
            // when it is given one — so the only place these can come from is the last whole-file
            // result. Without this the greying of an unused using blinked out on every keystroke
            // and came back on the next full pass.
            bool alwaysCarried = s_wholeFileOnlyIds.Contains(diagnostic.Id);

            if (!alwaysCarried)
            {
                // The fresh pass covered the whole file for this id, so it has already said
                // everything there is to say about it.
                if (!spanLimited(diagnostic.Id))
                    continue;

                // Inside what was re-analysed: the fresh answer replaces this one, including by
                // being absent, which is how a fixed warning disappears.
                if (analyzedSpan.IntersectsWith(mapped))
                    continue;
            }

            if (seen.Add((diagnostic.Id, mapped)))
                result.Add(Relocate(diagnostic, mapped, edit));
        }

        return result.ToImmutable();
    }

    /// <summary>Ids no span-restricted pass can produce, whoever asked for the span.</summary>
    private static readonly ImmutableHashSet<string> s_wholeFileOnlyIds =
        ImmutableHashSet.Create(StringComparer.Ordinal, "CS8019", "IDE0005");

    /// <summary>
    /// The same finding, at the span the edit moved it to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rebuilt rather than relocated: <c>Diagnostic.WithLocation</c> is internal to the
    /// compiler layer, and the public factory that takes an already-rendered message is the only
    /// way to carry one across trees without the arguments it was formatted from. Everything the
    /// LSP shape reads — id, category, severity, warning level, custom tags, properties, the
    /// message itself — is passed through; what is lost is the descriptor's identity, which
    /// nothing downstream compares by reference.
    /// </para>
    /// <para>
    /// The additional locations move with it. They are what a code fix uses to find the rest of
    /// what it has to change — the other half of a redundant cast, the declaration behind an unused
    /// value — so dropping them would leave the squiggle in the right place and its fix pointing
    /// at the wrong one.
    /// </para>
    /// </remarks>
    private static Diagnostic Relocate(Diagnostic diagnostic, TextSpan span, MemberEdit edit)
    {
        var descriptor = diagnostic.Descriptor;
        return Diagnostic.Create(
            diagnostic.Id,
            descriptor.Category,
            diagnostic.GetMessage(),
            diagnostic.Severity,
            diagnostic.DefaultSeverity,
            descriptor.IsEnabledByDefault,
            diagnostic.WarningLevel,
            diagnostic.IsSuppressed,
            descriptor.Title,
            descriptor.Description,
            descriptor.HelpLinkUri,
            Location.Create(edit.Tree, span),
            RelocateAll(diagnostic.AdditionalLocations, edit),
            customTags: descriptor.CustomTags,
            properties: diagnostic.Properties);
    }

    private static IEnumerable<Location>? RelocateAll(
        IReadOnlyList<Location> locations, MemberEdit edit)
    {
        if (locations.Count == 0)
            return null;

        var moved = new List<Location>(locations.Count);
        foreach (var location in locations)
        {
            if (!location.IsInSource)
                moved.Add(location);
            else if (Map(location.SourceSpan, edit.Change) is { } mapped)
                moved.Add(Location.Create(edit.Tree, mapped));
        }

        return moved;
    }
}

/// <summary>
/// Caches analyzer diagnostics per document version so the expensive pass runs once per edit
/// instead of once per request. The key mirrors the pull-diagnostics resultId scheme
/// (text checksum + dependent semantic version), so a cache hit and an "unchanged" pull
/// report agree on what "same world" means.
/// </summary>
internal static class AnalyzerDiagnosticCache
{
    /// <summary>
    /// Documents to keep analyzer results for — the floor of a cap that follows the working set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This bounds the findings payload only — the "this version was analyzed" fact lives in
    /// <see cref="s_analyzedVersions"/> and is never trimmed, so crossing this ceiling costs
    /// memory pressure relief, not correctness: an evicted-but-unmoved file still answers
    /// "unchanged" and the editor keeps displaying what it already has. It stopped being safe to
    /// treat this as "a runaway guard, never a working limit" the day a solution with more
    /// analyzed files than entries turned every eviction into a re-report and the Problems panel
    /// into a sawtooth; the split is what makes the number allowed to be wrong.
    /// </para>
    /// <para>
    /// Wrong, but not free. A fixed cap below the working set still churns through the sweep's
    /// full-report path: any dependent-semantic-version move makes every document in the project
    /// stale at once, and each stale document whose findings were evicted is served a fallback,
    /// downgraded to a ":c" id, and queued for a recompute — whose store then evicts the next
    /// document's findings. On a solution with ~2400 analyzed documents against a cap of 2048
    /// that was a permanent treadmill: the trim warning below fired every dozen seconds for hours
    /// and the convergence warning never stopped. So the effective cap scales with how many
    /// documents have actually been analyzed (see <see cref="Trim"/>), and this constant is the
    /// floor, with <see cref="MaxEntriesCeiling"/> as the genuine runaway guard.
    /// </para>
    /// </remarks>
    private const int MaxEntries = 2048;

    /// <summary>
    /// The cap the working set can grow the cache to before eviction wins after all. Far above any
    /// solution this daemon has met; a working set beyond it trades the treadmill for memory, and
    /// the trim warning names the condition either way.
    /// </summary>
    private const int MaxEntriesCeiling = 16384;

    private static readonly ConcurrentDictionary<DocumentId, Entry> s_entries = new();
    private static readonly object s_gate = new();
    private static readonly Dictionary<(DocumentId, string), DiagnosticFlight<ImmutableArray<Diagnostic>>> s_inFlight = new();

    internal static Func<Document, Task>? BeforeComputeAsyncForTesting { get; set; }
    internal static Func<Task>? BeforeRetireAsyncForTesting { get; set; }
    internal static Func<Task, Task>? WaitForRetirementAsyncForTesting { get; set; }
    private static long s_invalidationGeneration;

    /// <summary>
    /// The last version each document was fully analyzed as — the fact the result id is built
    /// from, kept apart from the findings so that evicting one cannot forget the other.
    /// </summary>
    /// <remarks>
    /// They were one record, and that coupling is what made the Problems panel oscillate on a
    /// large solution: the id encodes "analyzers ran for this version", so when <see cref="Trim"/>
    /// evicted a findings entry the file's id moved, the next sweep re-reported it without its
    /// analyzer findings, queued a recompute, and that recompute's own Trim evicted the next
    /// file's entry — a treadmill that never converged once the working set outgrew
    /// <see cref="MaxEntries"/>. This map is a version string per document, small enough to never
    /// need trimming, so eviction is now invisible to the protocol: an unmoved file answers
    /// "unchanged" and the editor keeps showing the findings it already holds. Only a real
    /// invalidation (<see cref="Evict"/>, <see cref="Clear"/>) removes the fact.
    /// </remarks>
    private static readonly ConcurrentDictionary<DocumentId, string> s_analyzedVersions = new();

    /// <summary>Runaway guard for <see cref="s_analyzedVersions"/>: a reloaded solution mints new
    /// DocumentIds, so entries can only accumulate across reloads. Far above any real document
    /// count — clearing costs one re-report wave, so it must never fire in normal use.</summary>
    private const int MaxAnalyzedVersions = 65536;

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

    /// <summary>Stamp orders use for eviction. Request ordering is tracked separately, under
    /// the same lock as result publication, so a cache read cannot invalidate a pending write.</summary>
    private sealed record Entry(string Version, ImmutableArray<Diagnostic> Diagnostics, long Stamp);

    /// <summary>The cache key for a document, or null when it cannot be versioned.</summary>
    public static async Task<string?> GetVersionAsync(Document document, CancellationToken ct)
    {
        try
        {
            var text = await document.GetTextAsync(ct);
            var semanticVersion = await document.Project.GetDependentSemanticVersionAsync(ct);
            return $"{ContentHash(text)}:{semanticVersion}";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Null is not a harmless miss: an unversionable document is re-reported under a
            // never-matching id on every sweep, and its analyzer pass can never be cached. A
            // swallowed reason here once left that cascade with nothing to search but a guess.
            Services.ServiceLog.Warn(
                $"Could not derive a diagnostics version for '{document.Name}': {ex}",
                key: "diagnostics-version-derivation");
            return null;
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SourceText, string>
        s_contentHashes = new();

    /// <summary>
    /// A hash that is equal exactly when <see cref="SourceText.ContentEquals"/> is true, however
    /// the instance was produced.
    /// </summary>
    /// <remarks>
    /// Not <see cref="SourceText.GetChecksum"/>, which describes the instance's construction as
    /// much as its content: <c>SourceText.From(string)</c> defaults to SHA-1, loader texts to
    /// SHA-256, and a stream-read text hashes its raw bytes, byte-order mark included. The same
    /// file therefore took a different id depending on whether the open-buffer overlay or the disk
    /// loader happened to back its document — and the overlay legitimately swaps between the two
    /// when the base solution forks under it (a watched-file apply, a project add). Every such swap
    /// flipped the result id of every open-but-unedited file and re-bound it for a change that
    /// touched nothing. Hashing the characters makes the id a fact about the content alone.
    /// Cached per instance, like the checksum it replaces.
    /// </remarks>
    private static string ContentHash(SourceText text) =>
        s_contentHashes.GetValue(text, static t =>
        {
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
                System.Security.Cryptography.HashAlgorithmName.SHA256);

            char[] chunk = System.Buffers.ArrayPool<char>.Shared.Rent(16 * 1024);
            try
            {
                for (int at = 0; at < t.Length; at += chunk.Length)
                {
                    int count = Math.Min(chunk.Length, t.Length - at);
                    t.CopyTo(at, chunk, 0, count);
                    hash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                        chunk.AsSpan(0, count)));
                }
            }
            finally
            {
                System.Buffers.ArrayPool<char>.Shared.Return(chunk);
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        });

    /// <summary>Whether this exact document version has already been analyzed. An analyzed
    /// document with no findings is a real answer, not a miss — distinguishing the two is what
    /// keeps the pull path from re-queueing a background pass on every request.</summary>
    /// <remarks>
    /// This is the fact the result id encodes, and it deliberately survives <see cref="Trim"/>:
    /// it answers "has this version been analyzed", not "are the findings still in memory". A
    /// caller about to <em>serve</em> findings wants <see cref="HasStoredFindings"/> instead —
    /// an analyzed-but-evicted version reports true here and false there, and the gap between
    /// the two is what a full report must bridge with a fallback plus a recompute.
    /// </remarks>
    public static bool IsComputed(Document document, string? version) =>
        version is not null
        && s_analyzedVersions.TryGetValue(document.Id, out var analyzed)
        && analyzed == version;

    /// <summary>Whether the findings for this exact document version are actually in the cache,
    /// ready to serve. False for a version that was analyzed and then trimmed — the caller must
    /// fall back and queue a recompute, and must not stamp its report as the analyzed answer.</summary>
    public static bool HasStoredFindings(Document document, string? version) =>
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
    /// Payload-based, not <see cref="IsComputed"/>: a failed pass over an analyzed-but-evicted
    /// version leaves that answering true, and refreshing on it would re-pull, miss, recompute,
    /// fail, and refresh again — the loop this gate exists to break.
    /// </remarks>
    public static bool LastComputeStored(Document document, string? version) =>
        HasStoredFindings(document, version);

    /// <summary>Cached diagnostics, computing and storing them on a miss.</summary>
    public static async Task<ImmutableArray<Diagnostic>> GetOrComputeAsync(
        Document document, CancellationToken ct)
    {
        if (!LspFeatureOptions.AnalyzerDiagnostics)
            return ImmutableArray<Diagnostic>.Empty;

        // Stored findings, not IsComputed: an analyzed version whose payload was trimmed is
        // exactly the case a recompute is queued for, and answering it "already computed" with
        // the empty set would make the eviction permanent.
        var version = await GetVersionAsync(document, ct);
        if (version is null)
            return await AnalyzerService.RunDocumentAnalyzersAsync(document, ct);

        ct.ThrowIfCancellationRequested();
        var key = (document.Id, version);
        long invalidationGeneration = Volatile.Read(ref s_invalidationGeneration);
        DiagnosticFlight<ImmutableArray<Diagnostic>>? retiring = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            DiagnosticFlight<ImmutableArray<Diagnostic>> work;
            bool joined;
            lock (s_gate)
            {
                if (retiring is not null && (retiring.Invalidated || s_invalidationGeneration != invalidationGeneration))
                    return ImmutableArray<Diagnostic>.Empty;
                s_latestRequested[document.Id] = version;
                if (HasStoredFindings(document, version))
                    return TryGet(document, version);
                if (!s_inFlight.TryGetValue(key, out work!))
                    s_inFlight.Add(key, work = new(flight => ComputeAsync(document, version, flight)));
                joined = !work.Abandoned;
                if (joined)
                    work.Waiters++;
            }

            if (!joined)
            {
                Task retirement = work.Work.Value;
                await (WaitForRetirementAsyncForTesting is { } wait ? wait(retirement) : retirement).WaitAsync(ct);
                retiring = work;
                continue;
            }

            bool released = false;
            void ReleaseWaiter()
            {
                bool cancel;
                lock (s_gate)
                {
                    if (released)
                        return;
                    released = true;
                    cancel = --work.Waiters == 0 && !work.Completed;
                    if (cancel)
                        work.Abandoned = true;
                }
                if (cancel)
                    work.Cancel();
            }

            // Register before starting the lazy: Roslyn can bind synchronously before its first
            // incomplete await, and cancellation must still stop that work on the caller thread.
            using var registration = ct.Register(ReleaseWaiter);
            try { return await work.Work.Value.WaitAsync(ct); }
            finally { ReleaseWaiter(); }
        }
    }

    private static async Task<ImmutableArray<Diagnostic>> ComputeAsync(
        Document document, string version, DiagnosticFlight<ImmutableArray<Diagnostic>> work)
    {
        var key = (document.Id, version);
        try
        {
            if (BeforeComputeAsyncForTesting is { } beforeCompute)
                await beforeCompute(document).WaitAsync(work.Token);

            // One waiter only cancels its wait; the last waiter, eviction or configuration change
            // cancels shared work. The token covers compilation and the analyzer-slot wait,
            // where AnalyzerService's per-analysis time budget has not started yet.
            work.Token.ThrowIfCancellationRequested();
            var run = await RunAsync(document, version, work.Token);

            lock (s_gate)
            {
                // Both checks must be atomic with the write. A detached flight was invalidated
                // by Clear/Evict; a different latest version was superseded by an editor request.
                if (run.Failed || work.Abandoned
                    || !s_inFlight.TryGetValue(key, out var current) || !ReferenceEquals(current, work)
                    || !s_latestRequested.TryGetValue(document.Id, out var latest) || latest != version)
                    return run.Diagnostics;

                s_entries[document.Id] = new Entry(version, run.Diagnostics, Interlocked.Increment(ref s_clock));
                if (s_analyzedVersions.Count > MaxAnalyzedVersions)
                    s_analyzedVersions.Clear();
                s_analyzedVersions[document.Id] = version;
                Trim();
                return run.Diagnostics;
            }
        }
        catch (OperationCanceledException) when (work.Token.IsCancellationRequested)
        {
            // Expected abandonment/invalidation, not a failed analyzer. Nothing is stored or marked analyzed,
            // so background callers have no result to publish and no reason to request refresh.
            return ImmutableArray<Diagnostic>.Empty;
        }
        finally
        {
            if (BeforeRetireAsyncForTesting is { } beforeRetire)
                await beforeRetire();
            lock (s_gate)
            {
                work.Completed = true;
                if (s_inFlight.TryGetValue(key, out var current) && ReferenceEquals(current, work))
                {
                    s_inFlight.Remove(key);
                    if (!s_entries.ContainsKey(document.Id)
                        && !s_inFlight.Keys.Any(key => key.Item1 == document.Id))
                        s_latestRequested.TryRemove(document.Id, out _);
                }
            }
            work.Dispose();
        }
    }

    /// <summary>
    /// One member's span when that is all that changed, the whole file otherwise.
    /// </summary>
    /// <remarks>
    /// The splice needs two things to agree: an edit the differ can attribute to a single member,
    /// and a cached result computed for exactly the version that edit started from. The second is
    /// checked here rather than inside <see cref="MemberEditAnalysis"/> because each cache has its
    /// own entry and they can be a version apart — the compiler half is computed on the fast push
    /// phase and this one ~1500ms later, and a document closed and reopened in between has one and
    /// not the other.
    /// </remarks>
    private static async Task<AnalyzerService.AnalyzerRun> RunAsync(
        Document document, string version, CancellationToken ct)
    {
        MemberEditAnalysis.Observe(document, version);

        if (await MemberEditAnalysis.TryComputeAsync(document, version, ct) is not { } edit
            || !s_entries.TryGetValue(document.Id, out var prior)
            || prior.Version != edit.BaseVersion)
        {
            return await AnalyzerService.RunDocumentAnalyzersWithStatusAsync(document, ct);
        }

        var run = await AnalyzerService.RunMemberSpanAnalyzersAsync(document, edit.MemberSpan, ct);

        // A pass that gave up has nothing to splice; a whole-file result (no restricted subset)
        // needs no splicing. Both go back as they are.
        if (run.Failed || run.SpanLimitedIds is not { } spanLimited)
            return run;

        return run with
        {
            Diagnostics = MemberEditAnalysis.Splice(
                prior.Diagnostics, run.Diagnostics, edit, edit.MemberSpan, spanLimited.Contains),
        };
    }

    // Both of these forward to CompilerDiagnosticCache, which holds the other half of the same
    // document's diagnostics under the same key. Every caller that drops one wants both dropped —
    // a removed document, a reloaded project, an .editorconfig edit that re-severities compiler
    // ids as readily as analyzer ones — and forwarding here is what keeps the two from drifting as
    // call sites are added.

    public static void Evict(DocumentId documentId)
    {
        List<DiagnosticFlight<ImmutableArray<Diagnostic>>> invalidated = [];
        lock (s_gate)
        {
            s_invalidationGeneration++;
            s_entries.TryRemove(documentId, out _);
            s_analyzedVersions.TryRemove(documentId, out _);
            s_latestRequested.TryRemove(documentId, out _);
            foreach (var key in s_inFlight.Keys.Where(key => key.Item1 == documentId).ToArray())
            {
                s_inFlight[key].Invalidated = true;
                invalidated.Add(s_inFlight[key]);
                s_inFlight.Remove(key);
            }
            MemberEditAnalysis.Forget(documentId);
            CompilerDiagnosticCache.Evict(documentId);
        }
        foreach (var work in invalidated)
            work.Cancel();
    }

    /// <summary>Drops everything — used when analyzer configuration changes (.editorconfig edits).</summary>
    public static void Clear()
    {
        DiagnosticFlight<ImmutableArray<Diagnostic>>[] invalidated;
        lock (s_gate)
        {
            s_invalidationGeneration++;
            s_entries.Clear();
            s_analyzedVersions.Clear();
            invalidated = [.. s_inFlight.Values];
            foreach (var work in invalidated)
                work.Invalidated = true;
            s_inFlight.Clear();
            s_latestRequested.Clear();
            MemberEditAnalysis.Clear();
            CompilerDiagnosticCache.Clear();
        }
        foreach (var work in invalidated)
            work.Cancel();
    }

    /// <summary>Test seam: a tiny ceiling makes eviction reachable without 2048 real documents.</summary>
    internal static int? MaxEntriesOverrideForTesting { get; set; }

    /// <summary>Findings entries trimmed since the last capacity log line.</summary>
    private static int s_trimmedSinceLogged;

    private static void Trim()
    {
        // Sized to the analyzed working set, not to a guess made before it existed: every entry
        // evicted while its document is still being swept re-enters through a fallback report and
        // a recompute, so a cap below the working set does not save the memory — it converts it
        // into repeated analyzer passes. The slack covers the analyses in flight between a store
        // and its s_analyzedVersions stamp. s_analyzedVersions accumulates dead DocumentIds across
        // solution reloads, which can only make the cap generous; the ceiling is what bounds that.
        int maxEntries = MaxEntriesOverrideForTesting
            ?? Math.Clamp(s_analyzedVersions.Count + 256, MaxEntries, MaxEntriesCeiling);
        if (s_entries.Count <= maxEntries)
            return;

        int trimming = s_entries.Count - maxEntries;

        // Deliberately NOT s_analyzedVersions: trimming findings frees memory, and must stay
        // invisible to the result id — see the field's remarks. Only the payload goes.
        //
        // Worth a log line, batched: sustained trimming means the analyzed working set is larger
        // than this cache, which is exactly the condition that used to present as a silently
        // oscillating Problems panel and cost a day of guessing. One line per ~256 evictions keeps
        // the record without turning steady state into spam.
        if ((s_trimmedSinceLogged += trimming) >= 256 && MaxEntriesOverrideForTesting is null)
        {
            Services.ServiceLog.Warn(
                $"Analyzer findings cache trimmed {s_trimmedSinceLogged} entries since last report "
                + $"(cap {maxEntries}, {s_analyzedVersions.Count} documents analyzed). The working "
                + "set is larger than the cache; evicted files re-analyze on their next real change.",
                key: "analyzer-cache-trim");
            s_trimmedSinceLogged = 0;
        }

        // ConcurrentDictionary's own ToArray() takes its snapshot under the table locks. Ordering
        // the dictionary directly does not: LINQ's buffer sizes itself from an unlocked Count read
        // and then copies, so a removal landing in between — a concurrent Trim, or Forget for a
        // closed document — leaves default(KeyValuePair) holes at the tail that the key selector
        // dereferences into a NullReferenceException.
        foreach (var stale in s_entries.ToArray().OrderBy(e => e.Value.Stamp).Take(trimming))
        {
            if (!s_entries.TryRemove(stale))
                continue;

            // The guard record goes with the entry. s_latestRequested is written on every compute
            // request and was only ever cleared wholesale, so the closed documents the sweep queues
            // through RecomputeInBackground accumulated in it for the daemon's lifetime while their
            // entries were being trimmed away underneath them.
            //
            // Only here, and never as a general "drop any key with no entry" rule: the guard is
            // written before the entry exists, so such a rule could delete a newer pass's guard
            // between registration and completion. A trimmed document may still have a newer
            // version running; keep its guard until that flight finishes.
            if (!s_inFlight.Keys.Any(key => key.Item1 == stale.Key))
                s_latestRequested.TryRemove(stale.Key, out _);
            MemberEditAnalysis.Forget(stale.Key);
        }
    }
}

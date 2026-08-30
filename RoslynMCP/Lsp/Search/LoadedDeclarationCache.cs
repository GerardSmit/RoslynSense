using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMCP.Lsp.Search;

/// <summary>
/// The loaded solution's declarations, one flat entry per document, keyed by text version — the
/// corpus a Search Everywhere keystroke actually reads.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of what a warm search cost without it. Each keystroke swept every document
/// through <see cref="TopLevelSyntaxTreeIndex.GetIndexAsync"/> — a checksum derivation and a cache
/// lookup per document — and then fetched the text of every document that matched to turn spans
/// into lines. On a few thousand documents that is most of a second per keystroke, spent
/// re-deriving answers that had not changed since the last one. Measured on the solution that
/// prompted it: 0.8s warm, 2.7s whenever background churn had invalidated Roslyn's per-document
/// caches. The matching itself was always milliseconds — <see cref="SearchEverywhere.SearchNames"/>
/// runs the same matcher over the same shape of data in about a hundred.
/// </para>
/// <para>
/// An entry is exactly what matching needs — name, container, kind, line positions — extracted
/// once per document <em>version</em> and reused until the text moves. The version check is one
/// cheap await against state the workspace already holds, so a keystroke's sweep degenerates to a
/// dictionary lookup per document plus the matching. Extraction happens on the first search after
/// an edit for just the edited documents, and during warmup for the rest (see
/// <c>SolutionWarmup.SweepIndexesAsync</c>).
/// </para>
/// <para>
/// Kin to <see cref="NameIndex"/>, which builds the same shape from disk before the solution
/// exists; this one is fed by the workspace and therefore follows every unsaved buffer. The two
/// stay separate because their keys are different worlds: a file's length-and-mtime says nothing
/// about a dirty buffer, and a text version means nothing before a solution exists.
/// </para>
/// </remarks>
internal static class LoadedDeclarationCache
{
    /// <summary>What matching needs to know about one document, and nothing else.</summary>
    internal sealed record DocumentDeclarations(
        string Path, bool IsGenerated, IReadOnlyList<NameDeclaration> Declarations);

    private sealed record Entry(VersionStamp Version, DocumentDeclarations Declarations);

    private static readonly ConcurrentDictionary<DocumentId, Entry> s_cache = new();

    /// <summary>
    /// The document's declarations, extracted now if its text has moved since last time. Null for
    /// a document that has no path or will not read — one missing row, never a failed search.
    /// </summary>
    public static async Task<DocumentDeclarations?> GetAsync(Document document, CancellationToken ct)
    {
        if (document.FilePath is not { Length: > 0 } path)
            return null;

        VersionStamp version;
        try
        {
            version = await document.GetTextVersionAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        if (s_cache.TryGetValue(document.Id, out var cached) && cached.Version == version)
            return cached.Declarations;

        var extracted = await ExtractAsync(document, path, ct).ConfigureAwait(false);
        if (extracted is null)
            return null;

        s_cache[document.Id] = new Entry(version, extracted);
        return extracted;
    }

    /// <summary>
    /// Drops entries for documents the solution no longer holds.
    /// </summary>
    /// <remarks>
    /// A reloaded solution mints new <see cref="DocumentId"/>s, so without this the previous
    /// generation's entries would sit in the dictionary for the life of the daemon, each rooting
    /// its declaration list. Called after a sweep — the one place that has the live document set in
    /// hand — and only once the dead weight is real: reconciling on every keystroke would cost a
    /// set-build per search to reclaim nothing.
    /// </remarks>
    public static void Reconcile(IReadOnlyList<Document> documents)
    {
        if (s_cache.Count <= documents.Count + 512)
            return;

        var alive = documents.Select(document => document.Id).ToHashSet();
        foreach (var id in s_cache.Keys)
        {
            if (!alive.Contains(id))
                s_cache.TryRemove(id, out _);
        }
    }

    /// <summary>Test seam: forgets everything, so a test starts from a cold cache.</summary>
    internal static void Clear() => s_cache.Clear();

    /// <summary>
    /// One document's declarations with their spans already turned into lines — the conversion is
    /// paid here, once per version, rather than per search by every document that matched.
    /// </summary>
    private static async Task<DocumentDeclarations?> ExtractAsync(
        Document document, string path, CancellationToken ct)
    {
        TopLevelSyntaxTreeIndex? index;
        SourceText text;
        try
        {
            index = await TopLevelSyntaxTreeIndex.GetIndexAsync(document, ct).ConfigureAwait(false);
            if (index is null)
                return null;

            text = await document.GetTextAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A document that will not parse or read — a stale generated file, a file the
            // workspace has since lost — is one missing row, never a failed search.
            return null;
        }

        var declarations = new List<NameDeclaration>(index.DeclaredSymbolInfos.Length);
        foreach (var info in index.DeclaredSymbolInfos)
        {
            // An index restored from disk can outlive the text it described by a moment; a span
            // past the end of the file would throw rather than merely point somewhere odd.
            if (info.Span.End > text.Length)
                continue;

            var span = text.Lines.GetLinePositionSpan(info.Span);
            declarations.Add(new NameDeclaration(
                info.Name,
                info.FullyQualifiedContainerName,
                info.Kind,
                span.Start.Line,
                span.Start.Character,
                span.End.Line,
                span.End.Character));
        }

        return new DocumentDeclarations(path, SearchFileRules.IsGenerated(path), declarations);
    }
}

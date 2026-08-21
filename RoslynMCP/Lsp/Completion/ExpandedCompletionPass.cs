using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services;
using RoslynCompletionList = Microsoft.CodeAnalysis.Completion.CompletionList;
using RoslynCompletionOptions = Microsoft.CodeAnalysis.Completion.CompletionOptions;
using RoslynItem = Microsoft.CodeAnalysis.Completion.CompletionItem;

namespace RoslynMCP.Lsp.Completion;

/// <summary>
/// The expensive half of a completion list — the expanded (import-completion) providers — kept off
/// the request's critical path, the way Visual Studio does it.
/// </summary>
/// <remarks>
/// <para>
/// VS's <c>CompletionSource</c> issues two <c>GetCompletionsAsync</c> calls: one restricted to
/// <see cref="ExpandedCompletionMode.NonExpandedItemsOnly"/>, awaited and shown, and one restricted
/// to <see cref="ExpandedCompletionMode.ExpandedItemsOnly"/>, started on a background task and
/// merged into a later refresh. The split is pure provider filtering, so nothing is computed twice.
/// </para>
/// <para>
/// The expanded providers stay expensive even with warm indexes — <c>ImportCompletionWarmer</c>
/// removes the index <em>build</em>, not the per-request work: every request re-derives the
/// namespaces in scope (<c>GetImportScopes</c>) and, after a dot, re-resolves every candidate
/// extension member against the receiver type and re-checks browsability. That is what this class
/// moves off the keystroke.
/// </para>
/// <para>
/// The background pass is memoized on (document, completion span start, text checksum), so the
/// request that pays for it is rarely the one that started it: the list is marked
/// <c>isIncomplete</c> whenever a prefix exists, the client re-queries at the same position, and
/// the finished pass merges into that request instead. The checksum in the key is the invalidation
/// — one keystroke gives a different key, and the stale entry is dropped rather than merged into a
/// buffer it no longer describes.
/// </para>
/// </remarks>
internal static class ExpandedCompletionPass
{
    /// <summary>
    /// How long a request waits for the expanded pass once its own list is ready. Long enough that
    /// a warm pass usually lands in the same response, short enough to be invisible next to the
    /// round trip; a miss costs nothing but one keystroke of delay, because the memoized task is
    /// still there for the next request at this position.
    /// </summary>
    internal static TimeSpan GraceWindow { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Test seam: the background pass awaits this before doing any work, which is how a
    /// test holds an expanded pass open instead of racing it.</summary>
    internal static volatile Task Gate = Task.CompletedTask;

    /// <summary>What a memoized pass is keyed by. The checksum is the document version: any edit
    /// produces a different one, and with it a different key.</summary>
    internal readonly record struct PassKey(DocumentId? Document, int SpanStart, string Checksum);

    private static readonly object s_gate = new();
    private static PassKey s_key;
    private static Task<RoslynCompletionList?>? s_task;

    /// <summary>Test seam: the memoized task, or <c>null</c> if none has been started.</summary>
    internal static Task<RoslynCompletionList?>? Pending
    {
        get { lock (s_gate) return s_task; }
    }

    /// <summary>Test seam: the key the memoized task was started for.</summary>
    internal static PassKey PendingKey
    {
        get { lock (s_gate) return s_key; }
    }

    /// <summary>Test seam: forgets the memoized pass.</summary>
    internal static void Reset()
    {
        lock (s_gate)
        {
            s_key = default;
            s_task = null;
        }
    }

    /// <summary>
    /// Starts — or hands back — the expanded pass for this position. Never cancelled by the request
    /// that starts it: the whole point is that it outlives the response and serves the next one.
    /// </summary>
    public static Task<RoslynCompletionList?> Start(
        CompletionService service,
        Document document,
        SourceText text,
        int caret,
        int spanStart,
        RoslynCompletionOptions options,
        CompletionTrigger trigger)
    {
        var key = new PassKey(document.Id, spanStart, Checksum(text));

        lock (s_gate)
        {
            // A faulted pass is not memoized: the next request should try again rather than
            // inherit a failure for as long as the buffer stays untouched.
            if (s_task is { } existing && s_key == key && !existing.IsFaulted && !existing.IsCanceled)
                return existing;

            s_key = key;
            return s_task = Task.Run(() => RunAsync(service, document, caret, options, trigger));
        }
    }

    private static async Task<RoslynCompletionList?> RunAsync(
        CompletionService service,
        Document document,
        int caret,
        RoslynCompletionOptions options,
        CompletionTrigger trigger)
    {
        try
        {
            await Gate;

            return await service.GetCompletionsAsync(
                document, caret, options, document.Project.Solution.Options, trigger,
                roles: null, cancellationToken: CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            // Import completion is an enrichment; the non-expanded list is a complete answer
            // without it, so a failure here degrades the list rather than the request.
            ServiceLog.Warn(
                $"Expanded completion pass failed: {ex.Message}",
                key: "expanded-completion");
            return null;
        }
    }

    /// <summary>
    /// The grace window: an already-finished pass is taken for free, an unfinished one is given
    /// <see cref="GraceWindow"/> to land in this response and otherwise left to the next one.
    /// </summary>
    /// <remarks>
    /// A pass whose items are served is dropped from the memo. It has done its job — the request
    /// that started it and the request that merged it are both answered — and holding it would
    /// pin a list that only looks current: what the expanded providers see also changes when the
    /// import-completion index behind them finishes building, which no checksum of the buffer
    /// reflects. Re-asking at an unchanged position is rare (a second Ctrl+Space), and is exactly
    /// where a fresh pass is what the user wants.
    /// </remarks>
    public static async Task<RoslynCompletionList?> WithinGraceAsync(
        Task<RoslynCompletionList?> pass, CancellationToken ct)
    {
        if (!pass.IsCompleted)
        {
            if (GraceWindow <= TimeSpan.Zero)
                return null;

            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var winner = await Task.WhenAny(pass, Task.Delay(GraceWindow, timer.Token));
            timer.Cancel();

            if (winner != pass)
                return null;
        }

        if (!pass.IsCompletedSuccessfully)
            return null;

        lock (s_gate)
        {
            if (ReferenceEquals(s_task, pass))
            {
                s_task = null;
                s_key = default;
            }
        }

        return pass.Result;
    }

    /// <summary>
    /// Both passes, as one list. Within a single <c>GetCompletionsAsync</c> call Roslyn's
    /// <c>DisplayNameToItemsMap</c> collapses items that would show the same text; split across two
    /// calls it no longer sees the pair, so the collapse happens here instead — and it is one-sided
    /// on purpose. An expanded item that repeats a display text the non-expanded pass already
    /// offered is the unimported spelling of something already in scope: the in-scope item wins,
    /// because committing it inserts nothing but the name.
    /// </summary>
    public static IReadOnlyList<RoslynItem> Merge(
        IReadOnlyList<RoslynItem> nonExpanded, IReadOnlyList<RoslynItem> expanded)
    {
        if (expanded.Count == 0)
            return nonExpanded;
        if (nonExpanded.Count == 0)
            return expanded;

        var seen = new HashSet<(string, string)>(nonExpanded.Count);
        foreach (var item in nonExpanded)
            seen.Add(DisplayKey(item));

        var merged = new List<RoslynItem>(nonExpanded.Count + expanded.Count);
        merged.AddRange(nonExpanded);
        foreach (var item in expanded)
        {
            if (seen.Add(DisplayKey(item)))
                merged.Add(item);
        }

        return merged;
    }

    /// <summary>What the user would see two of. The suffix is part of it because that is what
    /// separates <c>List</c> the type from <c>List&lt;&gt;</c> in the list.</summary>
    private static (string, string) DisplayKey(RoslynItem item) =>
        (item.DisplayText, item.DisplayTextSuffix ?? "");

    private static string Checksum(SourceText text)
    {
        ImmutableArray<byte> checksum = text.GetChecksum();
        return checksum.IsDefaultOrEmpty ? "" : Convert.ToHexString(checksum.AsSpan());
    }
}

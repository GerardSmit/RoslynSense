using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion.Providers;
using RoslynMCP.Services;

namespace RoslynMCP.Lsp;

/// <summary>
/// Keeps Roslyn's import-completion indexes (unimported types, unimported extension members) warm
/// in the background, so a completion request never has to build them on its own thread.
/// </summary>
/// <remarks>
/// <para>
/// The indexes are cached per project keyed by a content checksum, so any edit invalidates the
/// edited project's entry. Completion itself serves whatever entry exists — stale included — and
/// Roslyn re-queues a refresh after every request it answers. What that leaves uncovered is
/// exactly what this class covers: the entry that does not exist yet (a freshly loaded project,
/// where completion would silently omit every unimported type), and the entry that has been stale
/// since the last keystroke (where the refresh should not wait for the user to ask twice).
/// </para>
/// <para>
/// Warming goes through Roslyn's own <c>AsyncBatchingWorkQueue</c> (via the two
/// <c>QueueCacheWarmUpTask</c> entry points), which deduplicates repeated requests for the same
/// project and checksum-short-circuits a rebuild that would produce what is already cached. The
/// debounce here exists so that a typing burst queues one warm-up when the user pauses, not one
/// per keystroke — each queue run costs a background compilation of the project.
/// </para>
/// </remarks>
internal static class ImportCompletionWarmer
{
    /// <summary>How long after the last keystroke in a file before its project's indexes are
    /// re-warmed. Shorter than the diagnostics debounce (400ms) would waste compiles mid-burst;
    /// much longer would leave a window where the next completion serves a list missing the
    /// symbol the user just declared.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(600);

    private static readonly KeyedDebouncer s_debounce = new("ImportCompletionWarmer");

    /// <summary>Test seam: completes when the most recently scheduled warm-up has queued its
    /// work (not when the index build itself finishes — that is Roslyn's queue).</summary>
    internal static volatile Task LastScheduled = Task.CompletedTask;

    /// <summary>
    /// Every warm-up still in flight, not only the last one scheduled. Resolving the document is
    /// a project lookup, and for a file no loaded project holds it is a walk up the directory
    /// tree that opens every project it passes — a workspace load running on nobody's request
    /// thread. A test that leaves one running finds its fixture loaded into the cache a later
    /// test is counting, or made the most recently used solution a later sweep reads.
    /// </summary>
    private static readonly ConcurrentDictionary<Task, byte> s_inFlight = new();

    /// <summary>
    /// Schedules a warm-up of the project that owns <paramref name="filePath"/>. Debounced per
    /// file; <paramref name="immediate"/> skips the quiet period (didOpen — nothing is being
    /// typed, and the sooner a cold project's index exists the better).
    /// </summary>
    public static void Schedule(string filePath, bool immediate = false)
    {
        var run = s_debounce.Restart(filePath, immediate ? TimeSpan.Zero : Quiet, async ct =>
        {
            try
            {
                var document = await LspDocumentResolver.ResolveAsync(filePath, ct);
                if (document is not null)
                    Queue(document.Project);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Warming is an optimization; completion still answers without it (just possibly
                // stale, or missing import items until Roslyn's own post-request refresh runs).
                ServiceLog.Warn(
                    $"Could not warm import-completion indexes for '{Path.GetFileName(filePath)}': {ex.Message}",
                    key: $"import-warm:{filePath}");
            }
        });

        LastScheduled = run;
        Track(run);
    }

    private static void Track(Task run)
    {
        s_inFlight[run] = 0;
        run.ContinueWith(
            static finished => s_inFlight.TryRemove(finished, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Test seam: completes once every scheduled warm-up has resolved its document and queued
    /// (or declined) its work, so nothing it does lands in the test that runs next. Roslyn's own
    /// index build is not waited for; it touches no workspace.
    /// </summary>
    internal static async Task DrainForTestsAsync()
    {
        while (!s_inFlight.IsEmpty)
            await Task.WhenAll(s_inFlight.Keys.ToArray());
    }

    /// <summary>
    /// Queues both import-completion indexes of <paramref name="project"/> for a background
    /// rebuild. Idempotent per checksum: a queued rebuild that finds its cache entry current
    /// stops at the comparison.
    /// </summary>
    public static void Queue(Project project)
    {
        if (!project.SupportsCompilation)
            return;

        // Both internal, reached via Publicizer (see csproj) — the same route the completion
        // handler takes to Roslyn's internal GetCompletionsAsync overload.
        if (project.Services.GetService<ITypeImportCompletionService>() is { } types)
            types.QueueCacheWarmUpTask(project);

        ExtensionMemberImportCompletionHelper.SymbolComputer.QueueCacheWarmUpTask(project);
    }
}

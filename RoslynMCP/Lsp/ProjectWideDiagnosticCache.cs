using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace RoslynMCP.Lsp;

/// <summary>
/// The warnings a whole-compilation pass produces that binding one tree at a time cannot, cached
/// per project so the expensive pass runs once per declaration change rather than once per sweep.
/// </summary>
/// <remarks>
/// <para>
/// Binding a single tree answers for everything a reader would call a diagnostic of that file, with
/// one exception: the "declared but never used" family. Whether a private field is read anywhere is
/// a fact about the whole project, and Roslyn only works it out at the end of a full pass over
/// every method body — <c>MethodCompiler</c> runs that check only when no tree filter is in play,
/// so <see cref="Compilation.GetSemanticModel"/> structurally cannot report it. The sweep binds
/// stale trees because that is what makes it fast; this is what keeps it complete.
/// </para>
/// <para>
/// Keyed on the project's dependent semantic version, which moves on declaration changes and stays
/// put through body edits. That is deliberate and it is the whole reason this is affordable: a
/// keystroke inside a method leaves the entry valid, so typing never queues a full pass. The cost
/// is a bounded staleness — delete the last read of a field and its warning arrives when the next
/// declaration change does, not on that keystroke — which is the same eventual consistency the
/// analyzer cache already trades for the same reason.
/// </para>
/// </remarks>
internal static class ProjectWideDiagnosticCache
{
    /// <summary>
    /// The ids a whole-compilation pass adds, all of them from Roslyn's unused-member check.
    /// </summary>
    /// <remarks>
    /// A list rather than a computed set difference, because deriving it honestly would mean
    /// binding every tree a second time to subtract — the pass this exists to keep rare. It is
    /// pinned by a test that diffs the two passes over a file built to provoke both, so a Roslyn
    /// upgrade that adds a fifth id fails that test rather than quietly dropping the warning.
    /// </remarks>
    public static readonly FrozenSet<string> CompilationOnlyIds = new[]
    {
        "CS0067", // event is never used
        "CS0169", // field is never used
        "CS0414", // field is assigned but its value is never used
        "CS0649", // field is never assigned to
    }.ToFrozenSet(StringComparer.Ordinal);

    private sealed record Entry(string Version, FrozenDictionary<string, ImmutableArray<Diagnostic>> ByPath);

    private sealed class ProjectState
    {
        public readonly object Gate = new();
        public readonly Dictionary<string, Lazy<Task<bool>>> InFlight = new();
        public Entry? Cached;
        public string? LatestRequested;
    }

    // A clear detaches the entire state, including pending writes. Work already running can
    // finish for its callers, but cannot repopulate the cache after configuration invalidation.
    private static readonly ConcurrentDictionary<ProjectId, ProjectState> s_projects = new();

    internal static Func<Project, Task>? BeforeComputeAsyncForTesting { get; set; }

    public static async Task<string?> GetVersionAsync(Project project, CancellationToken ct)
    {
        try
        {
            return (await project.GetDependentSemanticVersionAsync(ct)).ToString();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Same cascade as the analyzer cache's version: a null here silently blocks the
            // project's whole-compilation warnings from ever refreshing, so the reason must land
            // in the log rather than vanish.
            Services.ServiceLog.Warn(
                $"Could not derive a project-wide diagnostics version for '{project.Name}': {ex}",
                key: "diagnostics-version-derivation");
            return null;
        }
    }

    public static bool IsComputed(Project project, string? version)
    {
        if (version is null || !s_projects.TryGetValue(project.Id, out var state))
            return false;

        lock (state.Gate)
            return state.Cached?.Version == version;
    }

    /// <summary>
    /// The previous pass's answer, used while the current one is still missing.
    /// </summary>
    /// <remarks>
    /// A stale unused-field warning is a far better report than none: blanking it on every
    /// declaration change would make the warning flicker out of the Problems panel and back on a
    /// cadence the user cannot predict, which is exactly the churn the sweep is meant to remove.
    /// </remarks>
    public static ImmutableArray<Diagnostic> TryGetAnyVersion(Project project, string? filePath) =>
        Lookup(project, filePath);

    private static ImmutableArray<Diagnostic> Lookup(Project project, string? filePath)
    {
        if (filePath is not { Length: > 0 } || !s_projects.TryGetValue(project.Id, out var state))
            return [];

        lock (state.Gate)
            return state.Cached is { } entry && entry.ByPath.TryGetValue(filePath, out var found)
                ? found : [];
    }

    /// <summary>Runs the pass, or joins the one already running for this version.</summary>
    public static async Task<bool> RefreshAsync(Project project, CancellationToken ct)
    {
        string? version = await GetVersionAsync(project, ct);
        if (version is null)
            return false;

        var state = s_projects.GetOrAdd(project.Id, static _ => new ProjectState());
        Lazy<Task<bool>> work;
        lock (state.Gate)
        {
            state.LatestRequested = version;
            if (state.Cached?.Version == version)
                return false;

            if (!state.InFlight.TryGetValue(version, out work!))
            {
                work = new Lazy<Task<bool>>(() => ComputeAndStoreAsync(project, version, state));
                state.InFlight.Add(version, work);
            }
        }

        // Cancellation belongs to this waiter. Only the computation removes its flight, so
        // canceling a sweep cannot start a duplicate full compilation on the next sweep.
        return await work.Value.WaitAsync(ct);
    }

    private static async Task<bool> ComputeAndStoreAsync(Project project, string version, ProjectState state)
    {
        try
        {
            if (BeforeComputeAsyncForTesting is { } beforeCompute)
                await beforeCompute(project);
            var computed = await ComputeAsync(project, version, CancellationToken.None);

            lock (state.Gate)
            {
                // Version strings have no ordering. Compare the requested version while holding
                // the same lock as the write, otherwise an older pass can overwrite a new one.
                if (state.LatestRequested != version
                    || !s_projects.TryGetValue(project.Id, out var current)
                    || !ReferenceEquals(current, state))
                    return false;

                bool changed = !Same(state.Cached ?? Empty, computed);
                state.Cached = computed;
                return changed;
            }
        }
        finally
        {
            lock (state.Gate)
                state.InFlight.Remove(version);
        }
    }

    private static async Task<Entry> ComputeAsync(Project project, string version, CancellationToken ct)
    {
        if (await project.GetCompilationAsync(ct) is not { } compilation)
            return new Entry(version, FrozenDictionary<string, ImmutableArray<Diagnostic>>.Empty);

        var byPath = compilation.GetDiagnostics(ct)
            .Where(d => CompilationOnlyIds.Contains(d.Id)
                        && d.Location.IsInSource
                        && d.Location.SourceTree?.FilePath is { Length: > 0 })
            .GroupBy(d => d.Location.SourceTree!.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(g => g.Key, g => g.ToImmutableArray(), StringComparer.OrdinalIgnoreCase);

        return new Entry(version, byPath);
    }

    private static readonly Entry Empty =
        new(string.Empty, FrozenDictionary<string, ImmutableArray<Diagnostic>>.Empty);

    private static bool Same(Entry before, Entry after)
    {
        if (before.ByPath.Count != after.ByPath.Count)
            return false;

        foreach (var (path, was) in before.ByPath)
        {
            if (!after.ByPath.TryGetValue(path, out var now) || was.Length != now.Length)
                return false;

            // By what the editor draws, not by identity: a new compilation hands back equal
            // findings as different objects, and refreshing on that would loop.
            for (int i = 0; i < was.Length; i++)
            {
                if (was[i].Id != now[i].Id
                    || was[i].Severity != now[i].Severity
                    || was[i].Location.SourceSpan != now[i].Location.SourceSpan)
                {
                    return false;
                }
            }
        }

        return true;
    }

    public static void Clear()
    {
        s_projects.Clear();
    }
}

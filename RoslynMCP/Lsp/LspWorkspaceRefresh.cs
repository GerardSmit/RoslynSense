using RoslynMCP.Services;

namespace RoslynMCP.Lsp;

/// <summary>
/// Turns "the loaded project set moved" into a refresh the connected editors will act on.
/// </summary>
/// <remarks>
/// <para>
/// The editor caches what it was told and has no way to learn that the answer changed underneath
/// it. Everything derived from the project set — reference counts in the gutter, cross-project
/// diagnostics, inlay hints — is computed from whatever was loaded at the time, and the set grows
/// whenever a search widens the workspace and vanishes when a workspace is thrown away.
/// </para>
/// <para>
/// This is the missing half of the behaviour reported as "go to reference works, but the code lens
/// beside it shows nothing — and after I closed the file and opened it again, the references
/// loaded". Find-references widens the solution as a deliberate part of what it does; the lens on
/// the same line was computed before that and was never asked again. Closing and reopening the
/// file was the user performing the invalidation by hand.
/// </para>
/// <para>
/// Debounced because a solution load raises the signal many times in a row — once per batch of
/// projects — and each refresh makes every open document re-pull its lenses and diagnostics.
/// Refreshing per batch during a load would have the client redo that work several times over
/// while the answer is still moving. One refresh once it settles is what the client needs.
/// </para>
/// </remarks>
internal static class LspWorkspaceRefresh
{
    /// <summary>
    /// Subscribes to <see cref="WorkspaceService.ProjectSetChanged"/>. Idempotent: every session
    /// calls it on attach, and the refresh goes to all of them regardless of which one asked.
    /// </summary>
    public static void Install() =>
        WorkspaceService.ProjectSetChanged = Schedule;

    private static void Schedule()
    {
        // The debounce and its ceiling both live in ScheduleRefresh, which is the one place that
        // knows how to coalesce these safely — it owns each token's lifetime rather than disposing
        // a superseded one mid-send, and it has a maximum wait so a steady stream of project-set
        // changes cannot hold the refresh off forever. This used to be a second, subtly different
        // implementation of the same thing, with both of those bugs.
        LspSessionRegistry.ScheduleRefresh(RefreshKind.All);

        // Not part of the debounced refresh above: that one carries LSP refresh kinds, and the
        // Solution Explorer is a custom view the protocol has no kind for. It draws which projects
        // are loaded, so it is stale the moment this fires.
        //
        // Debounced on its own timer, because this is raised once per (file, project) pair a
        // watched-file batch applies — a branch switch or a build dropping its output produces
        // thousands — and the extension answers each notification with a whole-tree refresh. The
        // signal carries no payload, so collapsing a burst into one loses nothing: the tree is
        // redrawn from what is loaded when it finally arrives.
        ScheduleProjectSetNotification();
    }

    /// <summary>Quiet period before the tree is told. Shorter than the refresh debounce beside it:
    /// this only redraws one view, and a stale tree is visible in a way a stale lens is not.</summary>
    private static readonly TimeSpan NotifyQuiet = TimeSpan.FromMilliseconds(300);

    private static readonly Debouncer s_notify = new("Lsp");

    private static void ScheduleProjectSetNotification() =>
        s_notify.Restart(NotifyQuiet, _ =>
        {
            LspSessionRegistry.NotifyProjectSetChanged();

            // The same settled moment is when to notice that the set moved because something
            // was unloaded — a restore watcher eviction after a build, an analyzer rebuild —
            // and put the bound solution back. Loads reaching here cost one missing-projects
            // check and nothing else.
            SolutionWarmup.EnsureLoaded();
            return Task.CompletedTask;
        });
}

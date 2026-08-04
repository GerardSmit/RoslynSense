using System.Runtime.CompilerServices;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Services.Designers;

namespace RoslynMCP.Lsp;

/// <summary>
/// Arms the designer watcher for an editor session, and feeds what it rewrites back into the
/// loaded workspace.
/// </summary>
/// <remarks>
/// <para>
/// Regenerating a <c>.designer.cs</c> on save is what makes a control added to markup visible to
/// its code-behind — the job Visual Studio does. It was started only by the MCP
/// <c>open_solution</c> tool, so it happened for AI chats and never for the editor: in VS Code,
/// adding <c>&lt;asp:Label ID="lblNew" runat="server" /&gt;</c> produced no field, and the
/// code-behind could not see the control at all.
/// </para>
/// <para>
/// The watcher belongs to the solution, not to the connection. One daemon serves one solution to
/// several editor windows and to MCP clients at once, so the first session through here starts it
/// and every later one finds it already open.
/// </para>
/// </remarks>
internal static class DesignerWatchBridge
{
    private static readonly Lock s_gate = new();

    /// <summary>
    /// Which services already have the handler attached. Weak keys: the handler is a static
    /// method, so the service holds the delegate rather than the other way round, and nothing
    /// here should be what keeps a service — and its watchers — alive.
    /// </summary>
    private static readonly ConditionalWeakTable<SolutionSessionService, object> s_subscribed = new();

    /// <summary>Called from <c>initialize</c>, once per connected editor.</summary>
    public static void Start(IServiceProvider services, InitializeParams p)
    {
        // Absent when designer generation is switched off, and for the bare service providers
        // tests construct a server with.
        if (services.GetService(typeof(SolutionSessionService)) is not SolutionSessionService session)
            return;

        Subscribe(session);

        if (SolutionToWatch(p) is not { } solutionPath)
            return;

        lock (s_gate)
        {
            // An open solution has already settled this daemon's watching policy — including an
            // open_solution that deliberately passed watch:false, which an editor connecting
            // afterwards must not overturn.
            if (session.IsWatching || session.SolutionPath is not null)
                return;

            var directories = PathHelper.GetProjectsFromSolution(solutionPath)
                .Select(Path.GetDirectoryName)
                .Where(directory => !string.IsNullOrEmpty(directory))
                .Select(directory => directory!);

            session.Open(solutionPath, directories, watch: true);
        }
    }

    /// <summary>
    /// The solution this session may watch, or <c>null</c> when it must not start a watcher.
    /// </summary>
    /// <remarks>
    /// The binding, not the client's root, is what the watcher follows: it is the solution this
    /// process was started for and the one its workspaces are loaded from. A window rooted
    /// somewhere else reached this daemon by accident, and rewriting generated files under a tree
    /// it does not own is not a side effect to take on a guess.
    /// </remarks>
    private static string? SolutionToWatch(InitializeParams p)
    {
        if (WorkspaceService.BoundSolutionPath is not { Length: > 0 } bound || !File.Exists(bound))
            return null;

        var roots = Roots(p);
        return roots.Count == 0 || roots.Any(root => Contains(root, bound)) ? bound : null;
    }

    private static List<string> Roots(InitializeParams p)
    {
        var uris = new List<string>();
        foreach (var folder in p.WorkspaceFolders ?? [])
            uris.Add(folder.Uri);
        if (p.RootUri is { Length: > 0 } rootUri)
            uris.Add(rootUri);

        var roots = new List<string>();
        foreach (var uri in uris)
        {
            try { roots.Add(LspConverters.UriToPath(uri)); }
            catch (UriFormatException) { }
        }

        return roots;
    }

    private static bool Contains(string directory, string path)
    {
        try
        {
            var relative = Path.GetRelativePath(directory, path);
            return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void Subscribe(SolutionSessionService session)
    {
        lock (s_gate)
        {
            if (s_subscribed.TryGetValue(session, out _))
                return;

            s_subscribed.Add(session, new object());
        }

        session.Regenerated += OnRegenerated;
    }

    private static void OnRegenerated(WatchedRegeneration entry) => _ = InvalidateAsync(entry);

    /// <summary>
    /// A regenerated designer is a compiled document whose text moved underneath the loaded
    /// snapshot. Until its project is evicted, the code-behind still binds against the old field
    /// set, so the editor keeps reporting the control that was just added as undefined.
    /// </summary>
    private static async Task InvalidateAsync(WatchedRegeneration entry)
    {
        try
        {
            // The markup is the fallback: it sits beside its designer, so it finds the same project.
            string changed = entry.DesignerPath is { Length: > 0 } designer ? designer : entry.SourcePath;

            foreach (var project in Handlers.WatchedFilesHandler.FindNearestProjectFiles(changed))
                await WorkspaceService.EvictProjectAsync(project);

            AnalyzerDiagnosticCache.Clear();
            await LspSessionRegistry.RequestRefreshAsync(RefreshKind.All);
        }
        catch (Exception ex)
        {
            // Reached from the watcher's callback chain, which has nobody to catch for it.
            Console.Error.WriteLine($"[Lsp] Designer refresh failed: {ex.Message}");
        }
    }
}

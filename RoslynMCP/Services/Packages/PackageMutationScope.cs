namespace RoslynMCP.Services.Packages;

/// <summary>
/// Collects the fallout of package changes so a batch pays for it once.
/// </summary>
/// <remarks>
/// Evicting the cached workspaces costs seconds and throws away every analyzer result in the
/// process. Doing that per package — which is what the install path used to do — turns updating
/// fifty packages into fifty full reloads, so a bulk update spent almost all of its time
/// rebuilding state it was about to invalidate again.
///
/// Passed explicitly rather than kept in an <see cref="AsyncLocal{T}"/>: ambient context is not
/// this codebase's idiom, and it would hide the most expensive thing these methods do from the
/// call site.
/// </remarks>
public sealed class PackageMutationScope : IAsyncDisposable
{
    /// <summary>
    /// What the editor needs done after packages change — clearing analyzer results and asking
    /// clients to re-request diagnostics, lenses and hints. Installed by the LSP layer, because
    /// an MCP-only process has no client to refresh.
    /// </summary>
    public static Func<CancellationToken, Task>? AfterMutation { get; set; }

    private readonly HashSet<string> _projects = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationToken _ct;
    private bool _disposed;

    public PackageMutationScope(CancellationToken ct = default) => _ct = ct;

    /// <summary>Records that a project's package set changed.</summary>
    public void Touch(string projectPath)
    {
        if (projectPath is { Length: > 0 })
            _projects.Add(Path.GetFullPath(projectPath));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_projects.Count == 0)
            return;

        foreach (string project in _projects)
            ProjectModel.ProjectEvaluationService.Evict(project);

        await WorkspaceService.EvictAllAsync(_ct);

        if (AfterMutation is { } after)
        {
            try { await after(_ct); }
            catch { /* refreshing the editor must not fail the operation that succeeded */ }
        }
    }
}

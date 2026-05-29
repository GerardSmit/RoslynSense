using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RoslynMCP.Config;

namespace RoslynMCP.Services;

internal sealed class WorkspacePreloadHostedService : IHostedService
{
    private readonly ILogger<WorkspacePreloadHostedService> _logger;
    private readonly IReadOnlyList<string>? _configuredPaths;
    private CancellationTokenSource? _cts;

    public WorkspacePreloadHostedService(
        ILogger<WorkspacePreloadHostedService> logger,
        EffectiveSettings settings)
    {
        _logger = logger;
        _configuredPaths = settings.Preload;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var projects = ResolveProjects();
        if (projects.Count == 0) return;

        _logger.LogInformation("[Preload] Warming {Count} project(s) in background...", projects.Count);

        // Projects of the same solution share one workspace, so warming the first member loads
        // the whole solution; skip the rest to avoid redundant cache-hit churn.
        var warmedSolutions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var projectPath in projects)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var owner = WorkspaceService.GetOwnerSolutionKey(projectPath);
            if (owner is not null && !warmedSolutions.Add(owner))
            {
                _logger.LogInformation(
                    "[Preload] Skipping '{Project}' (solution already warmed).", Path.GetFileName(projectPath));
                continue;
            }

            try
            {
                _logger.LogInformation("[Preload] Loading '{Project}'...", Path.GetFileName(projectPath));
                await WorkspaceService.GetOrOpenProjectAsync(projectPath, cancellationToken: cancellationToken);
                _logger.LogInformation("[Preload] Loaded '{Project}'.", Path.GetFileName(projectPath));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[Preload] Failed to load '{Project}': {Error}",
                    Path.GetFileName(projectPath), ex.Message);
            }
        }

        _logger.LogInformation("[Preload] Done.");
    }

    private List<string> ResolveProjects()
    {
        // Preload is OPT-IN. With no explicit `preload` configured we warm nothing and let the
        // workspace load projects lazily on first access (open file X -> load X + its references
        // only, not the whole solution). Auto-warming the nearest solution used to eagerly load
        // every project — exactly the memory cost we now avoid.
        if (_configuredPaths is null || _configuredPaths.Count == 0)
            return [];

        var result = new List<string>();
        foreach (var path in _configuredPaths)
        {
            var normalized = PathHelper.NormalizePath(path);
            if (!File.Exists(normalized)) continue;

            // An explicitly-configured solution still expands to all its projects (the user opted
            // in); configure specific .csproj paths instead to warm only those.
            if (PathHelper.IsSolutionFile(normalized))
                result.AddRange(PathHelper.GetProjectsFromSolution(normalized));
            else if (normalized.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                result.Add(normalized);
        }
        return result;
    }
}

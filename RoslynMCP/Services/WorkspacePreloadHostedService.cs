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

        foreach (var projectPath in projects)
        {
            if (cancellationToken.IsCancellationRequested) break;
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
        // Explicit empty list = disabled
        if (_configuredPaths is { Count: 0 })
            return [];

        var paths = _configuredPaths ?? AutoDiscoverSolutions();

        var result = new List<string>();
        foreach (var path in paths)
        {
            var normalized = PathHelper.NormalizePath(path);
            if (!File.Exists(normalized)) continue;

            if (PathHelper.IsSolutionFile(normalized))
                result.AddRange(PathHelper.GetProjectsFromSolution(normalized));
            else if (normalized.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                result.Add(normalized);
        }
        return result;
    }

    private static List<string> AutoDiscoverSolutions()
    {
        var solutions = PathHelper.FindSolutionFiles(Directory.GetCurrentDirectory());
        return solutions.Length > 0 ? [solutions[0]] : [];
    }
}

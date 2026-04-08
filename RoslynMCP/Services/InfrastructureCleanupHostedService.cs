using Microsoft.Extensions.Hosting;

namespace RoslynMCP.Services;

internal sealed class InfrastructureCleanupHostedService : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await WorkspaceService.EvictAllAsync(cancellationToken);
        AnalyzerService.DisposeHost();
        ProjectIndexCacheService.DisposeAll();
    }
}

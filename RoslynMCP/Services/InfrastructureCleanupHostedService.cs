using Microsoft.Extensions.Hosting;
using RoslynMCP.Services.ExternalSource;

namespace RoslynMCP.Services;

internal sealed class InfrastructureCleanupHostedService : IHostedService
{
    /// <summary>What the downloaded-symbols cache is allowed to occupy.</summary>
    private const long MaxSymbolCacheBytes = 2L * 1024 * 1024 * 1024;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Off the startup path: nothing waits on it, and a slow disk must not delay the first
        // request. Framework PDBs are tens of megabytes each, so this is the one cache that would
        // otherwise grow without bound.
        _ = Task.Run(() => ExternalSourceCache.PruneSymbols(MaxSymbolCacheBytes), cancellationToken);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await WorkspaceService.EvictAllAsync(cancellationToken);
        AnalyzerService.DisposeHost();
        ProjectIndexCacheService.DisposeAll();
        ShadowCopyService.DisposeIfCreated();
    }
}

using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

public class InfrastructureCleanupHostedServiceTests
{
    [Fact]
    public async Task WhenStartAsyncCalledThenCompletesSuccessfully()
    {
        var service = new InfrastructureCleanupHostedService();
        await service.StartAsync(CancellationToken.None);
        // StartAsync returns Task.CompletedTask — should not throw
    }

    [Fact]
    public void WhenServiceCreatedThenImplementsIHostedService()
    {
        var service = new InfrastructureCleanupHostedService();
        Assert.IsAssignableFrom<Microsoft.Extensions.Hosting.IHostedService>(service);
    }
}

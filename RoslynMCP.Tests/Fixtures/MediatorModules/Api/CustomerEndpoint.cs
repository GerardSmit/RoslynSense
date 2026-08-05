using MediatR;
using MediatorModules.Contracts;

namespace MediatorModules.Api;

public sealed class CustomerEndpoint(ISender sender)
{
    public Task<int> SyncAsync(string region, CancellationToken ct) =>
        sender.Send(new SyncCustomersCommand(region), ct);
}

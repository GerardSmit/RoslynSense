using MediatR;
using MediatorModules.Contracts;

namespace MediatorModules.Inventory;

public sealed class InventorySyncHandler : IRequestHandler<SyncCustomersCommand, int>
{
    public Task<int> Handle(SyncCustomersCommand request, CancellationToken cancellationToken) =>
        Task.FromResult(1);
}

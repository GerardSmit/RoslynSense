using MediatR;
using MediatorModules.Contracts;

namespace MediatorModules.Billing;

public sealed class BillingSyncHandler : IRequestHandler<SyncCustomersCommand, int>
{
    public Task<int> Handle(SyncCustomersCommand request, CancellationToken cancellationToken) =>
        Task.FromResult(2);
}

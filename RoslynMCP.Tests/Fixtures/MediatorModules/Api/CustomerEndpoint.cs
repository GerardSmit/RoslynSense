using MediatR;
using MediatorModules.Contracts;

namespace MediatorModules.Api;

public sealed class CustomerEndpoint(ISender sender, Zapto.Mediator.ISender zapto)
{
    public Task<int> SyncAsync(string region, CancellationToken ct) =>
        sender.Send(new SyncCustomersCommand(region), ct);

    /// <summary>
    /// The same dispatch through the extension method Contracts' generator emitted. The overload
    /// taking the request's constructor arguments names the message nowhere at the call site, so
    /// only the generated body says what is being sent — and that body is in another project's
    /// compilation.
    /// </summary>
    public ValueTask<int> SyncThroughGeneratedAsync(string region, CancellationToken ct) =>
        zapto.SyncCustomersCommandAsync(region, ct);
}

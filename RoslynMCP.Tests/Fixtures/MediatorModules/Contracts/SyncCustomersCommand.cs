using MediatR;

namespace MediatorModules.Contracts;

/// <summary>The message both modules handle. It lives here so that neither module has to know the
/// other exists — which is exactly what keeps them out of each other's dependency closure.</summary>
public sealed record SyncCustomersCommand(string Region) : IRequest<int>;

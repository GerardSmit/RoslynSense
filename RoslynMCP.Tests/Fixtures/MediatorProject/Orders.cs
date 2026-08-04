using MediatR;
using Zapto.Mediator;

namespace MediatorFixture.Orders;

public sealed record OrderDto(int Id, string Customer);

/// <summary>
/// A Zapto request. Named with a <c>Query</c> suffix on purpose: the generator strips only the
/// suffix its interface is named after, so this becomes <c>GetOrderQueryAsync</c> and not
/// <c>GetOrderAsync</c>.
/// </summary>
public sealed record GetOrderQuery(int Id) : IRequest<OrderDto>;

public sealed class GetOrderQueryHandler : Zapto.Mediator.IRequestHandler<GetOrderQuery, OrderDto>
{
    public ValueTask<OrderDto> Handle(
        IServiceProvider provider, GetOrderQuery request, CancellationToken cancellationToken) =>
        new(new OrderDto(request.Id, "Ada"));
}

/// <summary>Suffixed <c>Request</c>, so the generated method is <c>ArchiveOrderAsync</c>.</summary>
public sealed record ArchiveOrderRequest(int Id) : IRequest<bool>;

/// <summary>
/// Reaches its interface through the abstract base, which implements the interface member
/// explicitly — so this override is not an interface implementation and only the override chain
/// identifies it.
/// </summary>
public sealed class ArchiveOrderHandler : RequestHandler<ArchiveOrderRequest, bool>
{
    protected override bool Handle(IServiceProvider provider, ArchiveOrderRequest request) => true;
}

/// <summary>
/// A behaviour, not a handler. It names the message in its base list exactly as a handler does, and
/// must never become a navigation target.
/// </summary>
public sealed class GetOrderLogging : IPipelineBehavior<GetOrderQuery, OrderDto>
{
    public Task<OrderDto> Handle(
        GetOrderQuery request, Func<Task<OrderDto>> next, CancellationToken cancellationToken) =>
        next();
}

using MediatorFixture.Notifications;
using MediatorFixture.Orders;
using MediatR;

namespace MediatorFixture;

/// <summary>
/// The call sites. Every shape navigation has to recognise is here once, and each is on its own
/// line so a test can name the line it expects rather than counting results.
/// </summary>
public sealed class OrderController(
    MediatR.IMediator mediatr,
    Zapto.Mediator.IMediator zapto)
{
    /// <summary>MediatR's usual overload, where the single type argument is the response.</summary>
    public Task<OrderDto> GetViaMediatR(int id) =>
        mediatr.Send(new GetOrderQuery(id));

    /// <summary>Zapto's explicit form, where the message is a type argument.</summary>
    public ValueTask<OrderDto> GetViaZapto(int id) =>
        zapto.Send<GetOrderQuery, OrderDto>(new GetOrderQuery(id));

    /// <summary>The generated extension taking the request.</summary>
    public ValueTask<OrderDto> GetViaExtension(GetOrderQuery query) =>
        zapto.GetOrderQueryAsync(query);

    /// <summary>
    /// The generated extension taking the request's constructor arguments. This line names neither
    /// the request type nor anything of its shape, so only a search that starts from the generated
    /// method itself can find it.
    /// </summary>
    public ValueTask<OrderDto> GetViaExtensionArguments(int id) =>
        zapto.GetOrderQueryAsync(id);

    public ValueTask<bool> Archive(int id) =>
        zapto.Send<ArchiveOrderRequest, bool>(new ArchiveOrderRequest(id));

    public Task Announce(int orderId) =>
        mediatr.Publish(new OrderPlacedNotification(orderId));

    /// <summary>
    /// A request the caller built elsewhere, typed only as the marker it implements. Nothing static
    /// can say which handler this reaches, and answering anyway would be a guess.
    /// </summary>
    public Task<OrderDto> GetIndirect(IRequest<OrderDto> built) =>
        mediatr.Send(built);
}

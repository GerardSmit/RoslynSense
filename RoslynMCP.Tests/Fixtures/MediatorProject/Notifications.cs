using MediatR;

namespace MediatorFixture.Notifications;

/// <summary>
/// A MediatR notification. Two handlers, because several is the normal case for a notification
/// rather than an ambiguity to pick between.
/// </summary>
public sealed record OrderPlacedNotification(int OrderId) : INotification;

public sealed class SendReceipt : INotificationHandler<OrderPlacedNotification>
{
    public Task Handle(OrderPlacedNotification notification, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

public sealed class UpdateStock : INotificationHandler<OrderPlacedNotification>
{
    public Task Handle(OrderPlacedNotification notification, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

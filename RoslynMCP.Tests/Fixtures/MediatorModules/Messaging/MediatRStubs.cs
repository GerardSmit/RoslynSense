// The same minimal MediatR stubs the single-project mediator fixture carries, here in their own
// project so every module can reference the marker interfaces the way a real solution does —
// through a ProjectReference, not a source file they all happen to include.

// ReSharper disable CheckNamespace
namespace MediatR
{
    public interface IBaseRequest;

    public interface IRequest : IBaseRequest;

    public interface IRequest<out TResponse> : IBaseRequest;

    public interface INotification;

    public interface ISender
    {
        Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);

        Task<object?> Send(object request, CancellationToken cancellationToken = default);
    }

    public interface IPublisher
    {
        Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification;
    }

    public interface IMediator : ISender, IPublisher;

    public interface IRequestHandler<in TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
    }

    public interface IRequestHandler<in TRequest>
        where TRequest : IRequest
    {
        Task Handle(TRequest request, CancellationToken cancellationToken);
    }

    public interface INotificationHandler<in TNotification>
        where TNotification : INotification
    {
        Task Handle(TNotification notification, CancellationToken cancellationToken);
    }
}

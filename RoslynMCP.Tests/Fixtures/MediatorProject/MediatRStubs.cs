// Minimal stubs for MediatR's contracts and dispatchers, so the fixture builds without the
// package. Only the shapes navigation keys on are here: the marker interfaces (which Zapto.Mediator
// also takes from MediatR.Contracts and so shares outright), the dispatch methods, and the handler
// interfaces.

// ReSharper disable CheckNamespace
namespace MediatR
{
    public interface IBaseRequest;

    public interface IRequest : IBaseRequest;

    public interface IRequest<out TResponse> : IBaseRequest;

    public interface INotification;

    public interface IStreamRequest<out TResponse>;

    public readonly struct Unit
    {
        public static Unit Value => default;
    }

    public interface ISender
    {
        /// <summary>
        /// The overload nearly all MediatR code uses — and the one whose single type parameter is
        /// the <em>response</em>, not the request.
        /// </summary>
        Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);

        Task<object?> Send(object request, CancellationToken cancellationToken = default);

        IAsyncEnumerable<TResponse> CreateStream<TResponse>(
            IStreamRequest<TResponse> request, CancellationToken cancellationToken = default);
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

    public interface IStreamRequestHandler<in TRequest, out TResponse>
        where TRequest : IStreamRequest<TResponse>
    {
        IAsyncEnumerable<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
    }

    /// <summary>
    /// A behaviour wrapping a request, which names its message in a base list exactly as a handler
    /// does and must not be mistaken for one.
    /// </summary>
    public interface IPipelineBehavior<in TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        Task<TResponse> Handle(TRequest request, Func<Task<TResponse>> next, CancellationToken cancellationToken);
    }
}

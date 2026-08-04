// Minimal stubs for Zapto.Mediator. It takes its message markers from MediatR.Contracts, so only
// the dispatchers, the handler interfaces and the abstract bases are its own.
//
// Two shapes here are the ones a naive implementation gets wrong, and both are faithful to the
// real library: Handle takes a leading IServiceProvider, and RequestHandler<,> implements the
// interface member *explicitly* while exposing a differently shaped abstract Handle for the user to
// override — so a class deriving from it is not an interface implementer in Roslyn's eyes.

// ReSharper disable CheckNamespace
using MediatR;

namespace Zapto.Mediator
{
    public readonly record struct MediatorNamespace(string Value);

    public interface ISender
    {
        ValueTask<TResponse> Send<TRequest, TResponse>(
            TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest<TResponse>;

        ValueTask Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest;

        /// <summary>The namespaced twin every dispatch method has, which is why no argument is
        /// ever located by index.</summary>
        ValueTask<TResponse> Send<TRequest, TResponse>(
            MediatorNamespace ns, TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest<TResponse>;
    }

    public interface IPublisher
    {
        ValueTask Publish<TNotification>(
            TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification;
    }

    public interface IBackgroundPublisher
    {
        void Publish<TNotification>(TNotification notification)
            where TNotification : INotification;
    }

    public interface IMediator : ISender, IPublisher
    {
        IBackgroundPublisher Background { get; }
    }

    public interface IRequestHandler<in TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        ValueTask<TResponse> Handle(
            IServiceProvider provider, TRequest request, CancellationToken cancellationToken);
    }

    public interface INotificationHandler<in TNotification>
        where TNotification : INotification
    {
        ValueTask Handle(
            IServiceProvider provider, TNotification notification, CancellationToken cancellationToken);
    }

    public abstract class RequestHandler<TRequest, TResponse> : IRequestHandler<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        ValueTask<TResponse> IRequestHandler<TRequest, TResponse>.Handle(
            IServiceProvider provider, TRequest request, CancellationToken cancellationToken) =>
            new(Handle(provider, request));

        protected abstract TResponse Handle(IServiceProvider provider, TRequest request);
    }

    public interface IMediatorBuilder
    {
        IMediatorBuilder AddRequestHandler<TRequest, TResponse>(Func<TRequest, TResponse> handler)
            where TRequest : IRequest<TResponse>;

        IMediatorBuilder AddNotificationHandler<TNotification>(Action<TNotification> handler)
            where TNotification : INotification;
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class IgnoreHandlerAttribute : Attribute;
}

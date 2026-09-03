// The Zapto.Mediator half of the stubs, beside the MediatR ones for the same reason: every module
// reaches them through a ProjectReference rather than a shared source file.
//
// Only what a generated extension method needs to bind. The single-project fixture's ZaptoStubs.cs
// carries the rest of the library's shapes — the abstract bases, the namespaced overloads, the
// delegate registrations — and none of them is what this fixture is about.

// ReSharper disable CheckNamespace
using MediatR;

namespace Zapto.Mediator
{
    public interface ISender
    {
        ValueTask<TResponse> Send<TRequest, TResponse>(
            TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest<TResponse>;
    }

    public interface IPublisher
    {
        ValueTask Publish<TNotification>(
            TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification;
    }

    public interface IMediator : ISender, IPublisher;

    public interface IRequestHandler<in TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        ValueTask<TResponse> Handle(
            IServiceProvider provider, TRequest request, CancellationToken cancellationToken);
    }
}

using Grpc.Core;
using ProtoSolution.Widgets;

namespace ProtoSolution.Server;

/// <summary>Server-side implementation of <c>widgets.WidgetService</c>, in a project that only
/// reaches the contract through its ProjectReference on Contracts.</summary>
public class WidgetGrpcService : WidgetService.WidgetServiceBase
{
    public override Task<GetWidgetsByIdReply> GetWidgetsById(GetWidgetsByIdRequest request, ServerCallContext context)
    {
        var reply = new GetWidgetsByIdReply();

        foreach (var id in request.Ids)
        {
            reply.Widgets.Add(new Widget { Id = id, Label = "widget-" + id });
        }

        return Task.FromResult(reply);
    }

    public override Task<Widget> RenameWidget(RenameWidgetRequest request, ServerCallContext context)
    {
        return Task.FromResult(new Widget { Id = request.Id, Label = request.Label });
    }

    public override async Task WatchWidgets(WatchWidgetsRequest request, IServerStreamWriter<Widget> responseStream, ServerCallContext context)
    {
        foreach (var id in request.Ids)
        {
            if (context.CancellationToken.IsCancellationRequested)
            {
                return;
            }

            await responseStream.WriteAsync(new Widget { Id = id, Label = "watched-" + id });
        }
    }
}

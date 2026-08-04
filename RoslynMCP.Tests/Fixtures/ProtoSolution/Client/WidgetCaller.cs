using ProtoSolution.Widgets;

namespace ProtoSolution.Client;

/// <summary>Calls <c>widgets.WidgetService</c> through the generated client, from a third project
/// that knows nothing about the implementation in Server.</summary>
public class WidgetCaller
{
    private readonly WidgetService.WidgetServiceClient _client;

    public WidgetCaller(WidgetService.WidgetServiceClient client)
    {
        _client = client;
    }

    public async Task<List<string>> GetWidgetLabelsAsync(IEnumerable<long> ids)
    {
        var request = new GetWidgetsByIdRequest();
        request.Ids.Add(ids);

        var reply = await _client.GetWidgetsByIdAsync(request);

        var labels = new List<string>();
        foreach (var widget in reply.Widgets)
        {
            labels.Add(widget.Label);
        }

        return labels;
    }

    public async Task<string> RenameAsync(long id, string label)
    {
        var renamed = await _client.RenameWidgetAsync(new RenameWidgetRequest { Id = id, Label = label });

        return renamed.Label;
    }

    public async Task<List<long>> WatchAsync(IEnumerable<long> ids, CancellationToken cancellationToken)
    {
        var request = new WatchWidgetsRequest();
        request.Ids.Add(ids);

        var seen = new List<long>();

        using var call = _client.WatchWidgets(request, cancellationToken: cancellationToken);
        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            Widget widget = call.ResponseStream.Current;
            seen.Add(widget.Id);
        }

        return seen;
    }
}

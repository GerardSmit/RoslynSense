using ProtoFixture.Common;
using ProtoFixture.Widgets;

namespace ProtoFixture;

/// <summary>Calls <c>widgets.WidgetService</c> through the generated client.</summary>
public class WidgetClientCaller
{
    private readonly WidgetService.WidgetServiceClient _client;

    public WidgetClientCaller(WidgetService.WidgetServiceClient client)
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

    public async Task<string> GetImageReferenceAsync(long id)
    {
        var widget = await GetSingleWidgetAsync(id);

        return widget.ImageCase switch
        {
            Widget.ImageOneofCase.ImageUrl => widget.ImageUrl,
            Widget.ImageOneofCase.ImageHash => widget.ImageHash,
            _ => string.Empty,
        };
    }

    public async Task<string> GetOriginAttributeAsync(long id)
    {
        var widget = await GetSingleWidgetAsync(id);

        return widget.Attributes.TryGetValue("origin", out var origin) ? origin : string.Empty;
    }

    public async Task<string> DescribePlacementAsync(long id)
    {
        var widget = await GetSingleWidgetAsync(id);
        var placement = widget.Placement ?? new Widget.Types.Placement();

        return placement.Row + "x" + placement.Column + " (" + widget.Visibility + ")";
    }

    public async Task<int> CountGroupMembersAsync(IEnumerable<long> groupIds)
    {
        var request = new GetMembersForGroupsRequest();
        request.GroupIds.Add(groupIds);

        var reply = await _client.GetMembersForGroupsAsync(request);

        var total = 0;
        foreach (var entry in reply.GroupMembers)
        {
            total += entry.Value.Members.Count;
        }

        return total;
    }

    public async Task<List<long>> WatchCreatedWidgetIdsAsync(IEnumerable<long> ids, CancellationToken cancellationToken)
    {
        var request = new WatchWidgetsRequest { Channel = Channel.Beta };
        request.Ids.Add(ids);

        var created = new List<long>();

        using var call = _client.WatchWidgets(request, cancellationToken: cancellationToken);
        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            var widgetEvent = call.ResponseStream.Current;
            if (widgetEvent.Kind == WidgetEvent.Types.Kind.Created && widgetEvent.PayloadCase == WidgetEvent.PayloadOneofCase.Widget)
            {
                created.Add(widgetEvent.Widget.Id);
            }
        }

        return created;
    }

    public static string DescribeNote(Note note)
    {
        return note.Note_ + " [" + note.Channel + "]";
    }

    private async Task<Widget> GetSingleWidgetAsync(long id)
    {
        var request = new GetWidgetsByIdRequest();
        request.Ids.Add(id);

        var reply = await _client.GetWidgetsByIdAsync(request);

        return reply.Widgets.Count > 0 ? reply.Widgets[0] : new Widget();
    }
}

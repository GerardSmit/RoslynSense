using Grpc.Core;
using ProtoFixture.Common;
using ProtoFixture.Widgets;

namespace ProtoFixture;

/// <summary>Server-side implementation of the <c>widgets.WidgetService</c> service.</summary>
public class WidgetGrpcService : WidgetService.WidgetServiceBase
{
    public override Task<GetWidgetsByIdReply> GetWidgetsById(GetWidgetsByIdRequest request, ServerCallContext context)
    {
        var reply = new GetWidgetsByIdReply();

        foreach (var id in request.Ids)
        {
            var widget = new Widget
            {
                Id = id,
                Uuid = new UUID { Value = "uuid-" + id },
                Label = "widget-" + id,
                Channel = Channel.Alpha,
                ImageUrl = "https://widgets.invalid/" + id,
                Placement = new Widget.Types.Placement { Row = 1, Column = 2 },
                Visibility = Widget.Types.Visibility.Public,
            };
            widget.Attributes["origin"] = "fixture";

            reply.Widgets.Add(widget);
        }

        return Task.FromResult(reply);
    }

    public override Task<GetMembersForGroupsReply> GetMembersForGroups(GetMembersForGroupsRequest request, ServerCallContext context)
    {
        var reply = new GetMembersForGroupsReply();

        foreach (var groupId in request.GroupIds)
        {
            var members = new GroupMemberList();
            members.Members.Add(new GroupMember { WidgetId = groupId, Role = "owner" });

            reply.GroupMembers[groupId] = members;
        }

        return Task.FromResult(reply);
    }

    public override async Task WatchWidgets(WatchWidgetsRequest request, IServerStreamWriter<WidgetEvent> responseStream, ServerCallContext context)
    {
        foreach (var id in request.Ids)
        {
            if (context.CancellationToken.IsCancellationRequested)
            {
                return;
            }

            await responseStream.WriteAsync(new WidgetEvent
            {
                Kind = WidgetEvent.Types.Kind.Created,
                Widget = new Widget { Id = id, Channel = request.Channel },
            });

            await responseStream.WriteAsync(new WidgetEvent
            {
                Kind = WidgetEvent.Types.Kind.Deleted,
                DeletedUuid = new UUID { Value = "uuid-" + id },
            });
        }
    }
}

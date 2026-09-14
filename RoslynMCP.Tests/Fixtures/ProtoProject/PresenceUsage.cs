using ProtoFixture.Widgets;

namespace ProtoFixture;

public static class PresenceUsage
{
    public static int ClearImage(Widget widget)
    {
        if (widget.HasImageUrl) widget.ClearImageUrl();
        widget.ClearImage();
        return Widget.ImageUrlFieldNumber;
    }
}

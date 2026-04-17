<%@ Control Language="C#" CodeBehind="OrderItems.ascx.cs" Inherits="AspxProject.OrderItemsControl" %>
<asp:Repeater ID="rptOrderItems" runat="server" OnItemDataBound="rpt_OnItemDataBound">
    <ItemTemplate>
        <asp:Literal ID="litSizeRemark" runat="server" />
        <asp:Label ID="lblItemName" runat="server" />
    </ItemTemplate>
</asp:Repeater>

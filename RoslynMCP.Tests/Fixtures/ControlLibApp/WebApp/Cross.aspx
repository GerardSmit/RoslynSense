<%@ Page Language="C#" CodeBehind="Cross.aspx.cs" Inherits="WebApp.CrossPage" %>
<!DOCTYPE html>
<html>
<body>
    <form id="form1" runat="server">
        <asp:Repeater ID="rptOrders" runat="server" OnItemDataBound="rptOrders_ItemDataBound">
            <ItemTemplate>
                <asp:Label ID="lblCross" runat="server" />
            </ItemTemplate>
        </asp:Repeater>
    </form>
</body>
</html>

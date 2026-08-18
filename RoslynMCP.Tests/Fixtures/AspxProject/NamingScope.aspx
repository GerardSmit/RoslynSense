<%@ Page Language="C#" CodeBehind="NamingScope.aspx.cs" Inherits="AspxProject.NamingScopePage" %>
<!DOCTYPE html>
<html>
<body>
    <form id="form1" runat="server">
        <asp:Repeater ID="rptA" runat="server" OnItemDataBound="rptA_ItemDataBound">
            <ItemTemplate>
                <asp:Label ID="lblDup" runat="server" />
            </ItemTemplate>
        </asp:Repeater>
        <asp:Repeater ID="rptB" runat="server">
            <ItemTemplate>
                <asp:Label ID="lblDup" runat="server" />
            </ItemTemplate>
        </asp:Repeater>
    </form>
</body>
</html>

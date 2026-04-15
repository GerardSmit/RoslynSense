<%@ Page Language="C#" CodeBehind="Repeater.aspx.cs" Inherits="AspxProject.RepeaterPage" %>
<!DOCTYPE html>
<html>
<body>
    <form id="form1" runat="server">
        <asp:Repeater ID="rptItems" runat="server" OnItemDataBound="rpt_OnItemDataBound">
            <ItemTemplate>
                <asp:Button ID="btnAction" runat="server" Text="Click" />
                <asp:Label ID="lblName" runat="server" Text="Name" />
            </ItemTemplate>
        </asp:Repeater>
    </form>
</body>
</html>

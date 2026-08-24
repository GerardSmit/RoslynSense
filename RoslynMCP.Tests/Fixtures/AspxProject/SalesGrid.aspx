<%@ Page Language="C#" CodeBehind="SalesGrid.aspx.cs" Inherits="AspxProject.SalesGridPage" %>
<!DOCTYPE html>
<html>
<body>
    <form id="form1" runat="server">
        <asp:Repeater ID="rptInvoices" runat="server" ItemType="AspxProject.Invoice">
            <ItemTemplate>
                <asp:Label ID="lblReference" runat="server" Text='<%# Eval("Reference") %>' />
            </ItemTemplate>
        </asp:Repeater>
    </form>
</body>
</html>

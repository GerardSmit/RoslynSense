<%@ Page Language="C#" CodeBehind="Designer.aspx.cs" Inherits="AspxProject.DesignerPage" %>
<!DOCTYPE html>
<html>
<body>
    <form id="designerForm" runat="server">
        <asp:Label ID="lblHeading" runat="server" Text="Heading" />
        <asp:TextBox ID="txtName" runat="server" />
        <asp:Button ID="btnSave" runat="server" Text="Save" OnClick="BtnSave_Click" />
        <asp:Repeater ID="rptItems" runat="server">
            <ItemTemplate>
                <asp:Label ID="lblNested" runat="server" Text="nested" />
            </ItemTemplate>
        </asp:Repeater>
        <asp:Label ID="lblHandWritten" runat="server" Text="declared in code-behind" />
        <div><%= txtName.ClientID %></div>
    </form>
</body>
</html>

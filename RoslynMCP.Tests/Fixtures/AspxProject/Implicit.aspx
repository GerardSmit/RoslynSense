<%@ Page Language="C#" CodeBehind="Implicit.aspx.cs" Inherits="AspxProject.ImplicitPage" %>
<%@ Register TagPrefix="uc" Namespace="AspxProject.Controls" %>
<!DOCTYPE html>
<html>
<body>
    <%-- Nothing on this page writes a resource key. Every string it shows is asked for by the
         page's own localizer, from the control's id and from each column's unique name. --%>
    <form id="implicitForm" runat="server">
        <asp:Literal ID="litStatus" runat="server" />
        <asp:Label ID="lblStatus" runat="server" />
        <uc:ItemGrid ID="list" runat="server">
            <Columns>
                <uc:ItemGridColumn UniqueName="Amount" />
                <uc:ItemGridColumn UniqueName="Ordered" />
            </Columns>
        </uc:ItemGrid>
    </form>
</body>
</html>

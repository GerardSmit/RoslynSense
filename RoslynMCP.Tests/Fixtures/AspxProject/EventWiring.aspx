<%@ Page Language="C#" CodeBehind="EventWiring.aspx.cs" Inherits="AspxProject.EventWiringPage" %>
<!DOCTYPE html>
<html>
<script runat="server">
    protected override void OnLoad(System.EventArgs e)
    {
        base.OnLoad(e);
    }

    private int Doubled()
    {
        return Total() * 2;
    }
</script>
<body>
    <form id="wiringForm" runat="server">
        <asp:Button ID="btnWired" runat="server" Text="Wired" OnClick="Existing_Click" />
        <asp:Button ID="btnUnwired" runat="server" Text="Unwired" OnClick="MissingHandler" />
        <div><%= Total() %></div>
        <%
            // Total is only mentioned here, not called.
            string note = "Total";
        %>
    </form>
</body>
</html>

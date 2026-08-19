<%@ Page Language="C#" %>
<html>
<body>
    <form id="form1" runat="server">
        <asp:Literal ID="litCdn" runat="server" Text="<%$ AppSettings: CdnRoot %>" />
        <asp:Literal ID="litConn" runat="server" Text="<%$ ConnectionStrings: Main %>" />
        <asp:Literal ID="litProvider" runat="server" Text="<%$ ConnectionStrings: Main.ProviderName %>" />
    </form>
</body>
</html>

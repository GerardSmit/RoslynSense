<%@ Page Language="C#" CodeBehind="Localized.aspx.cs" Inherits="AspxProject.LocalizedPage" %>
<%@ Register TagPrefix="uc" Namespace="AspxProject.Controls" %>
<!DOCTYPE html>
<html>
<body>
    <form id="localizedForm" runat="server">
        <h1><%$ Resources: Heading %></h1>
        <asp:Label ID="lblHeading" runat="server" Text="<%$ Resources: Heading %>" />
        <asp:Label ID="lblCatalogue" runat="server" Text="<%$ Resources: Strings, Title %>" />
        <asp:Button ID="btnSave" runat="server" meta:resourcekey="btnSave" />
        <uc:LocalizedLabel ID="lblGreeting" runat="server" ResourceKey="Greeting" />
    </form>
</body>
</html>

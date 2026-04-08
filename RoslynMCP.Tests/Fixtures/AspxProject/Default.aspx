<%@ Page Language="C#" CodeBehind="Default.aspx.cs" Inherits="AspxProject.DefaultPage" %>
<!DOCTYPE html>
<html>
<head runat="server">
    <title>Test Page</title>
</head>
<body>
    <form id="form1" runat="server">
        <asp:Label ID="lblTitle" runat="server" Text="Hello World" />
        <asp:Button ID="btnSubmit" runat="server" Text="Submit" OnClick="BtnSubmit_Click" />
        <div>
            <%= DateTime.Now.ToString() %>
        </div>
        <div>
            <%: HttpUtility.HtmlEncode("test") %>
        </div>
        <% if (IsPostBack) { %>
            <p>This is a postback.</p>
        <% } %>
    </form>
</body>
</html>

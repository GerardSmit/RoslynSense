<%@ Control Language="C#" ClassName="HeaderControl" %>
<div class="header">
    <h1><%= Title %></h1>
    <asp:Label ID="lblSubtitle" runat="server" Text="Welcome" />
</div>
<%
    string Title = "Default Title";
    if (!IsPostBack)
    {
        lblSubtitle.Text = "Welcome to " + Title;
    }
%>

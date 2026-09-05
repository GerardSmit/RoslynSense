<%@ Control Language="C#" %>
<asp:Label runat="server" Text="Text stays literal" />
<%= new System.Web.UI.WebControls.Label().Text %>

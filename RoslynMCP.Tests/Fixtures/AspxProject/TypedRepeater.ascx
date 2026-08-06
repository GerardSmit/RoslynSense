<%@ Control Language="C#" CodeBehind="TypedRepeater.ascx.cs" Inherits="AspxProject.TypedRepeaterControl" %>
<asp:Repeater ID="rptTyped" runat="server" ItemType="System.String">
    <ItemTemplate>
        <li><%# Item.Length %></li>
    </ItemTemplate>
</asp:Repeater>

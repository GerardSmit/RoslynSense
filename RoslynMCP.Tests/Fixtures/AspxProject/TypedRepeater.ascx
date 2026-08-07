<%@ Control Language="C#" CodeBehind="TypedRepeater.ascx.cs" Inherits="AspxProject.TypedRepeaterControl" %>
<asp:Repeater ID="rptTyped" runat="server" ItemType="System.String">
    <ItemTemplate>
        <li><%# Item.Length %></li>
        <li><%# Container.DataItem %></li>
    </ItemTemplate>
</asp:Repeater>

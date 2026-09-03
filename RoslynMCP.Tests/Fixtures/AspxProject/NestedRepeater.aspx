<%@ Page Language="C#" %>
<!DOCTYPE html>
<html>
<body>
    <!--
        Repeaters inside repeaters, which is where a runtime id picks up more than one row number:
        the inner repeater's own id was numbered before the button's was, so the number the user
        pastes is not only at the end.
    -->
    <form id="form1" runat="server">
        <asp:Repeater ID="rptBaskets" runat="server">
            <ItemTemplate>
                <!-- The awkward twin: an ID somebody wrote that ends in a number of its own. -->
                <asp:Label ID="lblRow_2" runat="server" />
                <asp:Repeater ID="rptBasketRows" runat="server">
                    <ItemTemplate>
                        <asp:Button ID="btnRemoveRow" runat="server" Text="Remove" />
                    </ItemTemplate>
                </asp:Repeater>
            </ItemTemplate>
        </asp:Repeater>
    </form>
</body>
</html>

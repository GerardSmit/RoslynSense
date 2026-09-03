<%@ Page Language="C#" %>
<%@ Register Src="~/Controls/OrderItems.ascx" TagPrefix="uc" TagName="OrderItems" %>
<!DOCTYPE html>
<html>
<body>
    <form id="hostForm" runat="server">
        <!-- A tag whose control is an .ascx, so F12 on it has somewhere in markup to go. -->
        <uc:OrderItems runat="server" />
    </form>
</body>
</html>

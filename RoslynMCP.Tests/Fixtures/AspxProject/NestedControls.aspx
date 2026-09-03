<%@ Page Language="C#" %>
<%@ Register Src="~/Controls/OuterPanel.ascx" TagPrefix="uc" TagName="OuterPanel" %>
<!DOCTYPE html>
<html>
<body>
    <!-- The page end of a control tree that spans three files, which is what a runtime id names. -->
    <form id="pageForm" runat="server">
        <uc:OuterPanel runat="server" ID="ucOuter" />
    </form>
</body>
</html>

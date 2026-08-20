<%@ Control Language="C#" ClassName="OuterPanelControl" %>
<%@ Register Src="~/Controls/InnerPanel.ascx" TagPrefix="uc" TagName="InnerPanel" %>
<uc:InnerPanel runat="server" ID="ucInner">
    <asp:PlaceHolder runat="server" ID="phSlot">
        <!--
            Written here and rendered inside the inner control, which is the ordinary shape and the
            awkward one: the id names `ucInner`, but the file that declares this button is this one.
        -->
        <asp:LinkButton runat="server" ID="lnkDeep" />
    </asp:PlaceHolder>
</uc:InnerPanel>

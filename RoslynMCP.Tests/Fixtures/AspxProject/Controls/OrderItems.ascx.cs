namespace AspxProject;

public class OrderItemsControl : System.Web.UI.UserControl
{
    protected System.Web.UI.WebControls.Repeater rptOrderItems = null!;

    protected void rpt_OnItemDataBound(object sender, System.Web.UI.WebControls.RepeaterItemEventArgs e)
    {
        var lit = e.Item.FindControl("litSizeRemark") as System.Web.UI.WebControls.Literal;
        if (lit != null)
            lit.Text = "Size remark text";
    }
}

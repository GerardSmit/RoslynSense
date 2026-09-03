namespace AspxProject;

/// <summary>
/// Two repeaters whose templates declare the same <c>ID</c>, so FindControl navigation has to
/// choose: a lookup inside rptA's handler means rptA's label, a lookup outside any handler means
/// both, and a computed id means neither.
/// </summary>
public class NamingScopePage : System.Web.UI.Page
{
    protected System.Web.UI.WebControls.Repeater rptA = null!;
    protected System.Web.UI.WebControls.Repeater rptB = null!;

    protected void rptA_ItemDataBound(object sender, System.Web.UI.WebControls.RepeaterItemEventArgs e)
    {
        var scoped = e.Item.FindControl("lblDup") as System.Web.UI.WebControls.Label;
    }

    private void SaveAll()
    {
        var unscoped = rptA.FindControl("lblDup");

        string prefix = "lbl";
        var computed = rptA.FindControl(prefix + "Dup");
    }
}

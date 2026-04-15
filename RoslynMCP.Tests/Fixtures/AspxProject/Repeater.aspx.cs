namespace AspxProject;

public class RepeaterPage : System.Web.UI.Page
{
    protected System.Web.UI.WebControls.Repeater rptItems = null!;

    protected void rpt_OnItemDataBound(object sender, System.Web.UI.WebControls.RepeaterItemEventArgs e)
    {
        InitItem(e.Item);
        e.Item.SetText("btnAction", "Click me");
    }

    private void InitItem(System.Web.UI.Control item)
    {
        var btn = item.FindControl("btnAction") as System.Web.UI.WebControls.Button;
        var lbl = item.FindControl("lblName") as System.Web.UI.WebControls.Label;
    }
}

public static class ControlExtensions
{
    public static void SetText(this System.Web.UI.Control control, string name, object text)
    {
        var ctrl = control.FindControl(name);
        if (ctrl is System.Web.UI.WebControls.Label lbl) lbl.Text = text?.ToString();
        if (ctrl is System.Web.UI.WebControls.Button btn) btn.Text = text?.ToString();
    }
}

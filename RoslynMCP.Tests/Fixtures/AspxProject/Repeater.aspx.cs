namespace AspxProject;

public class RepeaterPage : System.Web.UI.Page
{
    protected System.Web.UI.WebControls.Repeater rptItems = null!;

    /// <summary>Uses the control the markup declares, so that go-to-definition on its ID has a
    /// code usage to report. Designer.aspx declares an <c>ID="rptItems"</c> of its own, which is
    /// what makes this the wrong answer for that page.</summary>
    protected override void OnLoad(System.EventArgs e)
    {
        base.OnLoad(e);
        rptItems.ItemDataBound += rpt_OnItemDataBound;
    }

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

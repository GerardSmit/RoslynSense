using ControlLib;

namespace WebApp;

/// <summary>Reaches its template control only through the referenced library's wrappers, so
/// navigation on the id literal proves the wrapper scan crossed the project reference.</summary>
public class CrossPage : System.Web.UI.Page
{
    protected System.Web.UI.WebControls.Repeater rptOrders = null!;

    protected void rptOrders_ItemDataBound(
        object sender, System.Web.UI.WebControls.RepeaterItemEventArgs e)
    {
        if (e.Item.TryFindControl<System.Web.UI.WebControls.Label>("lblCross", out var lbl))
        {
            lbl!.Text = "found";
        }
    }
}

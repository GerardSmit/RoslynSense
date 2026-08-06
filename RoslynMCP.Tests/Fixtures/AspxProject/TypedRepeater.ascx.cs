namespace AspxProject;

/// <summary>
/// A strongly-typed template: the Repeater declares ItemType, so `Item` inside its ItemTemplate is
/// a System.String and the editor has to know that.
/// </summary>
public class TypedRepeaterControl : System.Web.UI.UserControl
{
    protected System.Web.UI.WebControls.Repeater rptTyped = null!;
}

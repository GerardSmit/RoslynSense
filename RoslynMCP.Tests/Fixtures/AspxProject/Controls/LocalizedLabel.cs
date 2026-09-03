namespace AspxProject.Controls;

/// <summary>
/// A control that really declares a <c>ResourceKey</c> property, so the passthrough that stops
/// DNN's unprefixed <c>resourcekey</c> from reporting WFC0002 can be shown not to swallow a
/// property a control genuinely has.
/// </summary>
public class LocalizedLabel : System.Web.UI.WebControls.Label
{
    public string ResourceKey { get; set; } = "";
}

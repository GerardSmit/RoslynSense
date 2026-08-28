namespace AspxProject.Controls;

/// <summary>
/// A grid whose columns are not controls: they carry a <c>UniqueName</c> instead of an <c>ID</c>,
/// which is the shape every grid component in the WebForms world settled on and the reason a
/// heading's resource key is composed from an attribute rather than found by control id.
/// </summary>
[System.Web.UI.ParseChildren(true)]
public class ItemGrid : System.Web.UI.WebControls.WebControl
{
    public List<ItemGridColumn> Columns { get; } = [];
}

public class ItemGridColumn
{
    public string UniqueName { get; set; } = "";

    public string HeaderText { get; set; } = "";
}

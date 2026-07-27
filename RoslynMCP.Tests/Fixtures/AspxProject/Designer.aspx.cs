namespace AspxProject
{
    /// <summary>
    /// Code-behind for the designer-generation fixture. Deliberately declares one control field
    /// by hand so regeneration can be checked to skip it rather than emit a duplicate member.
    /// </summary>
    public partial class DesignerPage : System.Web.UI.Page
    {
        protected System.Web.UI.WebControls.Label lblHandWritten = null!;

        protected void BtnSave_Click(object sender, System.EventArgs e) { }
    }
}

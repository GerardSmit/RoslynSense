namespace AspxProject
{
    /// <summary>
    /// Code-behind for the designer-generation fixture. Deliberately declares one control field
    /// by hand so regeneration can be checked to skip it rather than emit a duplicate member.
    /// </summary>
    public partial class DesignerPage : System.Web.UI.Page
    {
        protected System.Web.UI.WebControls.Label lblHandWritten = null!;

        protected void BtnSave_Click(object sender, System.EventArgs e)
        {
            // Two carets for the go-to-definition contributor: one control whose field the
            // designer generates, and one the class above declares by hand. Deliberately not
            // btnSave — WebFormsIndexTests.ALensDoesNotCountItsOwnDeclaration needs one control
            // that nothing uses, and that is the one it picked.
            lblHeading.Text = "Heading";
            lblHandWritten.Text = "done";
        }
    }
}

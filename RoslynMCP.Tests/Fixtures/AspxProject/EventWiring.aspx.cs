namespace AspxProject
{
    /// <summary>
    /// Code-behind for the event-wiring fixture. One of the page's two buttons names a handler
    /// that deliberately does not exist, so the missing-handler diagnostic and its quick fix have
    /// something to report on.
    /// </summary>
    public partial class EventWiringPage : System.Web.UI.Page
    {
        protected System.Web.UI.HtmlControls.HtmlForm wiringForm = null!;
        protected System.Web.UI.WebControls.Button btnWired = null!;
        protected System.Web.UI.WebControls.Button btnUnwired = null!;

        protected void Existing_Click(object sender, System.EventArgs e) { }

        /// <summary>Called from the markup, so that references into code blocks have a symbol
        /// to look for. The markup also mentions the name in a comment and in a string.</summary>
        protected int Total() => 42;
    }
}

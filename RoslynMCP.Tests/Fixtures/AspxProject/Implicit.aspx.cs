using DotNetNuke.Services.Localization;

namespace AspxProject
{
    /// <summary>
    /// Code-behind for the implicit-binding fixture. It writes no key: the localizer builds one per
    /// control from its id, and one per grid column from its unique name, which is why the entries
    /// in this page's .resx have no counterpart anywhere in its markup.
    /// </summary>
    public partial class ImplicitPage : System.Web.UI.Page
    {
        /// <summary>What a page-wide localizer does, reduced to the one call that matters: the key
        /// is composed, never spelled out.</summary>
        protected string Caption(string controlId) =>
            Localization.GetString(controlId + ".Text", this);

        /// <summary>The same for a grid column, whose key carries the Header prefix in front of the
        /// name the column is known by.</summary>
        protected string Heading(string uniqueName) =>
            Localization.GetString("Header" + uniqueName + ".Text", this);
    }
}

using DotNetNuke.Services.Localization;

namespace AspxProject
{
    /// <summary>
    /// Code-behind for the localization fixture. The three calls are the same method name and the
    /// same arity: which overload binds is the only thing that decides where the root comes from.
    /// </summary>
    public partial class LocalizedPage : System.Web.UI.Page
    {
        /// <summary>The (string, string) overload, whose root really is argument 1.</summary>
        protected string FromAnotherPage() => Localization.GetString("Greeting", "~/Default.aspx");

        /// <summary>The (string, Control) overload. Argument 1 is a control, so the root is this
        /// page's own markup file rather than anything the call spells out.</summary>
        protected string FromThisPage() => Localization.GetString("Greeting", this);

        /// <summary>A root that is a parameter: nothing reads it, so the files are guessed from
        /// what sits near the call.</summary>
        protected string FromAnUnknownFile(string resourceFile) =>
            Localization.GetString("Heading", resourceFile);
    }
}

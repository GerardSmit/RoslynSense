// Minimal stubs so the ASPX parser can resolve asp:* controls and HTML server controls
// in tests without requiring a real System.Web reference.

// ReSharper disable CheckNamespace
#pragma warning disable CS0067 // Event is never used

namespace System.Web.UI
{
    public interface ITemplate { }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class ParseChildrenAttribute : Attribute
    {
        public ParseChildrenAttribute(bool childrenAsProperties) { }
        public bool ChildrenAsProperties { get; set; }
    }

    public class Control
    {
        public string ID { get; set; } = "";
        public Control FindControl(string id) => null!;
    }

    public class Page : Control
    {
        public bool IsPostBack { get; }
    }

    namespace HtmlControls
    {
        public class HtmlGenericControl : Control { }
        public class HtmlForm : Control { }
        public class HtmlHead : Control { }
        public class HtmlTitle : Control { }
        public class HtmlLink : Control { }
        public class HtmlImage : Control { }
    }

    namespace WebControls
    {
        public class WebControl : Control { }

        public class Label : WebControl
        {
            public string Text { get; set; } = "";
        }

        public class Button : WebControl
        {
            public string Text { get; set; } = "";
            public event EventHandler? Click;
        }

        public class TextBox : WebControl
        {
            public string Text { get; set; } = "";
            public event EventHandler? TextChanged;
        }

        public class LinkButton : WebControl
        {
            public string Text { get; set; } = "";
            public event EventHandler? Click;
            public string PostBackUrl { get; set; } = "";
        }

        [System.Web.UI.ParseChildren(true)]
        public class Repeater : WebControl
        {
            public System.Web.UI.ITemplate? ItemTemplate { get; set; }
            public System.Web.UI.ITemplate? AlternatingItemTemplate { get; set; }
            public System.Web.UI.ITemplate? HeaderTemplate { get; set; }
            public System.Web.UI.ITemplate? FooterTemplate { get; set; }
            public System.Web.UI.ITemplate? SeparatorTemplate { get; set; }
            public event EventHandler<RepeaterItemEventArgs>? ItemDataBound;
            public event EventHandler<RepeaterItemEventArgs>? ItemCreated;
        }

        public class RepeaterItem : Control { }

        public class RepeaterItemEventArgs : EventArgs
        {
            public RepeaterItem Item { get; } = null!;
        }
    }
}

namespace AspxProject
{
    public class DefaultPage : System.Web.UI.Page
    {
        protected System.Web.UI.WebControls.Label lblTitle = null!;
        protected System.Web.UI.WebControls.Button btnSubmit = null!;

        protected void BtnSubmit_Click(object sender, EventArgs e) { }
    }
}

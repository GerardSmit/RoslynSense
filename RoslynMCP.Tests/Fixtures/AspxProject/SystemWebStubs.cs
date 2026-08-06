// Minimal stubs so the ASPX parser can resolve asp:* controls and HTML server controls
// in tests without requiring a real System.Web reference.

// ReSharper disable CheckNamespace
#pragma warning disable CS0067 // Event is never used

namespace System.Web
{
    public class HttpContext
    {
        public static object GetGlobalResourceObject(string classKey, string resourceKey) => null!;

        public static object GetLocalResourceObject(string virtualPath, string resourceKey) => null!;
    }
}

namespace System.Web.UI
{
    public interface ITemplate { }

    public enum TemplateInstance
    {
        Multiple = 0,
        Single = 1,
    }

    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class TemplateInstanceAttribute : Attribute
    {
        public TemplateInstanceAttribute(TemplateInstance instances) => Instances = instances;
        public TemplateInstance Instances { get; }
    }

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

        /// <summary>Something a page can override from its markup, so that a member declared in a
        /// script block is a real override rather than a new method that happens to compile.</summary>
        protected virtual void OnLoad(EventArgs e) { }
    }

    public class Page : Control
    {
        public bool IsPostBack { get; }
    }

    public class UserControl : Control { }

    /// <summary>Single-instance template host, like the real UpdatePanel: controls inside its
    /// ContentTemplate still get designer fields.</summary>
    [ParseChildren(true)]
    public class UpdatePanel : Control
    {
        [TemplateInstance(TemplateInstance.Single)]
        public ITemplate? ContentTemplate { get; set; }
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

        public class Literal : WebControl
        {
            public string Text { get; set; } = "";
        }

        public class Label : WebControl
        {
            public string Text { get; set; } = "";
        }

        /// <summary>A container, so a test can put a control inside another control.</summary>
        public class Panel : WebControl
        {
            public string CssClass { get; set; } = "";
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

            /// <summary>Names the type a template's `Item` binds to.</summary>
            public string ItemType { get; set; } = "";
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

// Minimal stubs so the ASPX parser can resolve asp:* controls in the web project without a real
// System.Web reference. They live in the class library so the wrapper extensions below them can
// extend the same Control type the web project's markup binds to.

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

    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class TemplateContainerAttribute : Attribute
    {
        public TemplateContainerAttribute(Type containerType) => ContainerType = containerType;
        public Type ContainerType { get; }
    }

    public class Control
    {
        public string ID { get; set; } = "";
        public Control FindControl(string id) => null!;
    }

    public class Page : Control { }

    namespace WebControls
    {
        public class WebControl : Control { }

        public class Label : WebControl
        {
            public string Text { get; set; } = "";
        }

        [System.Web.UI.ParseChildren(true)]
        public class Repeater : WebControl
        {
            [System.Web.UI.TemplateContainer(typeof(RepeaterItem))]
            public System.Web.UI.ITemplate? ItemTemplate { get; set; }
            public event EventHandler<RepeaterItemEventArgs>? ItemDataBound;
        }

        public class RepeaterItem : Control
        {
            public object? DataItem { get; }
        }

        public class RepeaterItemEventArgs : EventArgs
        {
            public RepeaterItem Item { get; } = null!;
        }
    }
}

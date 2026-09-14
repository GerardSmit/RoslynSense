namespace System.Web.UI
{
    public class Control { }
    public class Page : Control { }
    public class UserControl : Control { }
}

namespace System.Web.UI.WebControls
{
    public class WebControl : System.Web.UI.Control { }
    public class Label : WebControl
    {
        public string Text { get; set; } = "";
    }
}

namespace AspxProject;

/// <summary>
/// A key written in C# rather than in markup, through the stock ASP.NET lookup: the virtual path
/// is argument 0 and the key is argument 1, so nothing about this call reads as a key by name.
/// </summary>
public static class ResourceHelper
{
    public static object Heading() =>
        System.Web.HttpContext.GetLocalResourceObject("~/Localized.aspx", "Heading");
}

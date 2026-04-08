namespace AspxProject;

public class PageHelper
{
    public static string FormatDate(DateTime date) => date.ToString("yyyy-MM-dd");

    public static bool IsPostBack { get; set; }
}

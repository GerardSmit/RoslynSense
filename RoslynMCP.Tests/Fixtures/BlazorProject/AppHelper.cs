namespace BlazorProject;

/// <summary>
/// A shared utility class referenced from Razor components.
/// </summary>
public static class AppHelper
{
    public static string FormatTitle(string title) => $"[{title}]";

    public static int DoubleValue(int value) => value * 2;
}

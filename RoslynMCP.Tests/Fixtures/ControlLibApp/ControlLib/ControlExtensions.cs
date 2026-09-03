using System.Web.UI;

namespace ControlLib;

/// <summary>
/// FindControl wrappers in a project the web project references, the way real sites keep them in
/// a shared utility library — the wrapper scan has to look across the project reference to see
/// these.
/// </summary>
public static class ControlExtensions
{
    public static T? FindControl<T>(this Control control, string id) where T : Control =>
        control.FindControl(id) as T;

    public static bool TryFindControl<T>(this Control control, string id, out T? result)
        where T : Control
    {
        result = control.FindControl(id) as T;
        return result is not null;
    }
}

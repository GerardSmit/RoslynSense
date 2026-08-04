// Minimal stubs for the DNN API the resource lookups are configured against, so the overload set
// that makes ParameterTypes necessary exists without a DotNetNuke reference.

// ReSharper disable CheckNamespace

namespace DotNetNuke.Entities.Portals
{
    public class PortalSettings
    {
        public int PortalId { get; set; }
    }
}

namespace DotNetNuke.Services.Localization
{
    /// <summary>
    /// The three two-argument overloads are the whole point: only <c>(string, string)</c> puts a
    /// resource root at index 1, and matching on name and arity alone would bind all three.
    /// </summary>
    public static class Localization
    {
        public static string GetString(string key) => key;

        public static string GetString(string key, string resourceFileRoot) => key;

        public static string GetString(string key, System.Web.UI.Control ctrl) => key;

        public static string GetString(string key, Entities.Portals.PortalSettings portalSettings) => key;
    }
}
